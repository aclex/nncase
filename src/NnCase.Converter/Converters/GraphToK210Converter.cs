﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using NnCase.Converter.Data;
using NnCase.Converter.Model;
using NnCase.Converter.Model.Layers;
using NnCase.Converter.Model.Layers.K210;
using NnCase.Converter.Transforms;
using RazorLight;
using TensorFlow;

namespace NnCase.Converter.Converters
{
    public class K210LayerBNConfig
    {
        public int Mul { get; set; }

        public int Shift { get; set; }

        public int Add { get; set; }
    }

    public class K210ConvLayerConfig
    {
        public byte[] Weights { get; set; }

        public int ArgX { get; set; }

        public int ShiftX { get; set; }

        public int ArgW { get; set; }

        public int ShiftW { get; set; }

        public int ArgAdd { get; set; }

        public int KernelType { get; set; }

        public int PoolType { get; set; }

        public bool IsDepthwise { get; set; }

        public int InputChannels { get; set; }

        public int OutputChannels { get; set; }

        public K210LayerBNConfig[] BNConfigs { get; set; }

        public int InputWidth { get; set; }

        public int InputHeight { get; set; }

        public int OutputWidth { get; set; }

        public int OutputHeight { get; set; }

        public int InputGroups { get; set; }

        public int InputRowLength { get; set; }

        public int OutputGroups { get; set; }

        public int OutputRowLength { get; set; }

        public int InputAddress { get; set; }

        public int OutputAddress { get; set; }

        public int OutputChannelsOnTime { get; set; }

        public int LoadTimes { get; set; }

        public int OneLoadKernelsSize { get; set; }

        public int PadValue { get; set; }

        public double InputScale { get; set; }

        public double InputBias { get; set; }

        public double OutputScale { get; set; }

        public double OutputBias { get; set; }

        public int InputSize { get; set; }

        public int OutputSize { get; set; }

        public int ActMul { get; set; }

        public int ActShift { get; set; }
    }

    public class K210GlobalAveragePoolConfig
    {
        public int KernelSize { get; set; }

        public double InputScale { get; set; }

        public double InputBias { get; set; }

        public double OutputScale { get; set; }

        public double OutputBias { get; set; }

        public ulong OutputAddress { get; set; }
    }

    public class K210CodeGenerationContext
    {
        public IReadOnlyList<K210ConvLayerConfig> Layers { get; set; }
        public string Prefix { get; set; }
    }

    public class GraphToK210Converter
    {
        private readonly Graph _graph;
        private readonly RazorLightEngine _templateEngine;

        public GraphToK210Converter(Graph graph)
        {
            _graph = graph;
            _templateEngine = new RazorLightEngineBuilder()
                .UseMemoryCachingProvider()
                .UseEmbeddedResourcesProject(typeof(GraphToK210Converter).Assembly, "Templates.K210")
                .Build();
        }

        public async Task ConvertAsync(Dataset dataset, GraphPlanContext planContext, string outputDir, string prefix)
        {
            _graph.Plan(planContext);

            var quantize = await GetMinMaxVars(dataset, planContext);
            var context = new ConvertContext { Quantization = quantize };
            foreach (var layer in _graph.Outputs)
                ConvertLayer(layer, context);

            context.ProcessMap.Clear();
            foreach (var layer in _graph.Outputs)
                InferenceLayer(layer, context);

            var codeGenContext = new K210CodeGenerationContext
            {
                Layers = context.InferenceOrders,
                Prefix = prefix
            };

            var code = await _templateEngine.CompileRenderAsync("Model", codeGenContext);
            File.WriteAllText(Path.Combine(outputDir, $"{prefix}.c"), code);
        }

        private void ConvertLayer(Layer layer, ConvertContext context)
        {
            if (!context.ProcessMap.GetValueOrDefault(layer))
            {
                context.ProcessMap[layer] = true;

                switch (layer)
                {
                    case InputLayer _:
                    case OutputLayer _:
                    case AveragePool2d _:
                    case L2Normalization _:
                    case Reshape _:
                        break;
                    case K210Conv2d l:
                        ConvertK210Conv2d(l, context);
                        break;
                    case K210GlobalAveragePool l:
                        ConvertK210GlobalAveragePool(l, context);
                        break;
                    case K210AddPadding _:
                    case K210RemovePadding _:
                        break;
                    default:
                        throw new NotSupportedException(nameof(layer));
                }

                foreach (var conn in layer.InputConnectors)
                {
                    var nextLayer = conn.Connection?.From.Owner;
                    if (nextLayer != null)
                        ConvertLayer(nextLayer, context);
                }
            }
        }

        private void ConvertK210Conv2d(K210Conv2d layer, ConvertContext context)
        {
            var config = new K210ConvLayerConfig { BNConfigs = new K210LayerBNConfig[layer.OutputChannels] };
            (var sw, var bw) = QuantizeWeights(layer.Weights, config);
            (var sx, var bx) = QuantizeInput(context.Quantization.Distributions[layer.Input.Connection.From], config);
            config.ArgAdd = (int)Math.Round(bw * bx * layer.KernelWidth * layer.KernelHeight);
            (var so, var bo) = QuantizeBiasAndOutput(layer.Bias, context.Quantization.Distributions[layer.Output], sw * sx, config);

            config.InputChannels = layer.InputChannels;
            config.OutputChannels = layer.OutputChannels;

            config.InputWidth = layer.Input.Dimensions[3];
            config.InputHeight = layer.Input.Dimensions[2];
            (config.InputGroups, config.InputRowLength) = GetRowLayout(config.InputWidth);
            config.OutputWidth = layer.Output.Dimensions[3];
            config.OutputHeight = layer.Output.Dimensions[2];
            (config.OutputGroups, config.OutputRowLength) = GetRowLayout(config.OutputWidth);

            config.KernelType = layer.KernelWidth == 3 ? 1 : 0;
            config.IsDepthwise = layer.Conv2dType == K210Conv2dType.DepthwiseConv2d;
            config.PoolType = (int)layer.PoolType;

            config.PadValue = (int)Math.Round(-bx);

            if (layer.Conv2dType == K210Conv2dType.Conv2d)
            {
                var kernelSize = (int)layer.Weights.Length;
                var oneChannelSize = layer.KernelWidth * layer.KernelHeight * layer.InputChannels;
                var oneLoadChannels = Math.Min(layer.OutputChannels, (int)Math.Floor(30 * 1024.0 / oneChannelSize));
                config.OneLoadKernelsSize = oneChannelSize * oneLoadChannels;
                config.LoadTimes = (int)Math.Ceiling(layer.OutputChannels / (double)oneLoadChannels);
                config.OutputChannelsOnTime = oneLoadChannels;
            }
            else
            {
                config.OneLoadKernelsSize = (int)layer.Weights.Length;
                config.LoadTimes = 1;
                config.OutputChannelsOnTime = layer.OutputChannels;
            }

            config.InputScale = 1 / sx;
            config.InputBias = bx / sx;
            config.OutputScale = 1 / so;
            config.OutputBias = bo / so;

            var inputOneLineChannels = Math.Min(layer.InputChannels, config.InputGroups);
            config.InputSize = config.InputRowLength * config.InputHeight * config.InputChannels / inputOneLineChannels;
            var outputOneLineChannels = Math.Min(layer.OutputChannels, config.OutputGroups);
            config.OutputSize = config.OutputRowLength * config.OutputHeight * config.OutputChannels / outputOneLineChannels;

            context.Layers.Add(layer, config);
        }

        private void ConvertK210GlobalAveragePool(K210GlobalAveragePool layer, ConvertContext context)
        {
            var config = new K210GlobalAveragePoolConfig
            {
            };


        }

        private void InferenceLayer(Layer layer, ConvertContext context)
        {
            if (!context.ProcessMap.GetValueOrDefault(layer))
            {
                context.ProcessMap[layer] = true;

                foreach (var conn in layer.InputConnectors)
                {
                    var inputLayer = conn.Connection?.From.Owner;
                    if (inputLayer != null)
                        InferenceLayer(inputLayer, context);
                }

                foreach (var output in layer.OutputConnectors)
                {
                    if (context.KPUMemoryMap.TryGetValue(output, out var node))
                    {
                        node.AddRef();
                    }
                    else
                    {
                        switch (layer)
                        {
                            case InputLayer l:
                                InferenceInputLayer(l, context);
                                break;
                            case OutputLayer _:
                            case AveragePool2d _:
                            case L2Normalization _:
                            case Reshape _:
                                break;
                            case K210Conv2d l:
                                InferenceK210Conv2d(l, context);
                                break;
                            case K210GlobalAveragePool l:
                                InferenceK210GlobalAveragePool(l, context);
                                break;
                            case K210AddPadding l:
                                InferenceK210AddPadding(l, context);
                                break;
                            case K210RemovePadding l:
                                InferenceK210RemovePadding(l, context);
                                break;
                            default:
                                throw new NotSupportedException(nameof(layer));
                        }
                    }
                }

                foreach (var conn in layer.InputConnectors)
                {
                    var output = conn.Connection?.From;
                    if (output != null)
                    {
                        if (context.KPUMemoryMap.TryGetValue(output, out var node))
                            node.Release();
                    }
                }
            }
        }

        private void InferenceInputLayer(InputLayer layer, ConvertContext context)
        {
            var k210ConvOut = layer.Output.Connections.Select(o => o.To.Owner).OfType<K210Conv2d>().FirstOrDefault();
            if (k210ConvOut != null)
            {
                var node = context.MemoryAllocator.Allocate(context.Layers[k210ConvOut].InputSize);
                context.KPUMemoryMap[layer.Output] = node;
            }
        }

        private void InferenceK210Conv2d(K210Conv2d layer, ConvertContext context)
        {
            var config = context.Layers[layer];
            var inputNode = context.KPUMemoryMap[layer.Input.Connection.From];
            var outputNode = context.MemoryAllocator.Allocate(config.OutputSize);
            context.KPUMemoryMap[layer.Output] = outputNode;

            config.InputAddress = inputNode.Start;
            config.OutputAddress = outputNode.Start;
            context.InferenceOrders.Add(config);
        }

        private void InferenceK210GlobalAveragePool(K210GlobalAveragePool layer, ConvertContext context)
        {
            //var config = context.Layers[layer];
            var inputNode = context.KPUMemoryMap[layer.Input.Connection.From];
            //var outputNode = context.MemoryAllocator.Allocate(config.OutputSize);
            context.KPUMemoryMap[layer.Output] = inputNode;

            //config.InputAddress = inputNode.Start;
            //config.OutputAddress = outputNode.Start;
            //context.InferenceOrders.Add(config);
        }

        private void InferenceK210AddPadding(K210AddPadding layer, ConvertContext context)
        {
            //var config = context.Layers[layer];
            var inputNode = context.KPUMemoryMap[layer.Input.Connection.From];
            //var outputNode = context.MemoryAllocator.Allocate(config.OutputSize);
            context.KPUMemoryMap[layer.Output] = inputNode;

            //config.InputAddress = inputNode.Start;
            //config.OutputAddress = outputNode.Start;
            //context.InferenceOrders.Add(config);
        }

        private void InferenceK210RemovePadding(K210RemovePadding layer, ConvertContext context)
        {
            //var config = context.Layers[layer];
            var inputNode = context.KPUMemoryMap[layer.Input.Connection.From];
            //var outputNode = context.MemoryAllocator.Allocate(config.OutputSize);
            context.KPUMemoryMap[layer.Output] = inputNode;

            //config.InputAddress = inputNode.Start;
            //config.OutputAddress = outputNode.Start;
            //context.InferenceOrders.Add(config);
        }

        private static (int groups, int rowLength) GetRowLayout(int width)
        {
            int groups, rowLength;

            if (width <= 16)
            {
                groups = 4;
                rowLength = 1;
            }
            else if (width <= 32)
            {
                groups = 2;
                rowLength = 1;
            }
            else
            {
                groups = 1;
                rowLength = (int)Math.Ceiling(width / 64.0);
            }

            return (groups, rowLength);
        }

        private async Task<QuantizationContext> GetMinMaxVars(Dataset dataset, GraphPlanContext planContext)
        {
            using (var session = new TFSession(planContext.TFGraph))
            {
                var connectors = new List<OutputConnector>();
                var toFetches = new List<TFOutput>();

                foreach (var output in planContext.TFOutputs)
                {
                    connectors.Add(output.Key);
                    if (!(output.Key.Owner is InputLayer))
                        toFetches.Add(output.Value);
                }

                var quantizationContext = new QuantizationContext { Outputs = connectors, PlanContext = planContext };
                await foreach (var batch in dataset.GetBatchesAsync())
                {
                    var input = batch.ToNHWC();
                    var runner = session.GetRunner();

                    runner.AddInput(planContext.Inputs.Values.First(), input);
                    foreach (var fetch in toFetches)
                        runner.Fetch(fetch);

                    var outputs = runner.Run();
                    RecordOutputs(new[] { input }.Concat(outputs).ToList(), quantizationContext);
                }

                return quantizationContext;
            }
        }

        private unsafe void RecordOutputs(IReadOnlyList<TFTensor> outputs, QuantizationContext context)
        {
            for (int i = 0; i < outputs.Count; i++)
            {
                var conn = context.Outputs[i];
                var span = new Span<float>(outputs[i].Data.ToPointer(), (int)outputs[i].TensorByteSize / 4);
                var newRange = GetRange(span);
                if (context.Distributions.TryGetValue(conn, out var range))
                    context.Distributions[conn] = range.EMA(0.01, newRange);
                else
                    context.Distributions.Add(conn, newRange);
            }
        }

        private static (double scale, double bias) QuantizeWeights(Tensor<float> weights, K210ConvLayerConfig config)
        {
            var buffer = weights.ToDenseTensor().Buffer.Span;
            (var scale, var bias) = GetRange(buffer).GetScaleBias();

            (var mul, var shift) = ExtractValueAndShift(bias, 24, 15);
            config.Weights = Quantize(buffer, scale, bias);
            config.ArgX = (int)Math.Round(mul);
            config.ShiftX = shift;
            return (scale, bias);
        }

        private static (double scale, double bias) QuantizeInput(Range range, K210ConvLayerConfig config)
        {
            (var scale, var bias) = range.GetScaleBias();
            (var mul, var shift) = ExtractValueAndShift(bias, 24, 15);
            config.ArgW = (int)Math.Round(mul);
            config.ShiftW = shift;
            return (scale, bias);
        }

        private static (double scale, double bias) QuantizeBiasAndOutput(Tensor<float> bias, Range range, double scale, K210ConvLayerConfig config)
        {
            (var so, var bo) = range.GetScaleBias();
            var scomb = so / scale;

            (var mul, var shift) = ExtractValueAndShift(scomb, 24, 255);
            var upscale = shift - 15;
            var postMul = Math.Round(mul) / mul * Math.Pow(2, upscale);

            for (int i = 0; i < config.BNConfigs.Length; i++)
            {
                var b = bias[i];

                config.BNConfigs[i] = new K210LayerBNConfig
                {
                    Mul = (int)Math.Round(mul),
                    Shift = 15,
                    Add = (int)Math.Round((b * so - bo) * postMul)
                };
            }

            QuantizeActivation(postMul, upscale, config);
            return (so, bo);
        }

        private static void QuantizeActivation(double postMul, int upScale, K210ConvLayerConfig config)
        {
            (var mul, var shift) = ExtractValueAndShift(1 / postMul, 16, 255);
            config.ActMul = 2;// (int)Math.Round(mul);
            config.ActShift = upScale + 1;// shift;
        }

        private static byte[] Quantize(Span<float> data, double scale, double bias)
        {
            var q = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                q[i] = (byte)Math.Clamp(Math.Round(data[i] * scale - bias), byte.MinValue, byte.MaxValue);
            return q;
        }

        private static Range GetRange(Span<float> data)
        {
            double min = double.MaxValue, max = double.MinValue;
            for (int j = 0; j < data.Length; j++)
            {
                min = Math.Min(min, data[j]);
                max = Math.Max(max, data[j]);
            }

            return new Range { Min = min, Max = max };
        }

        private static (double value, int shift) ExtractValueAndShift(double value, int maxBits, int maxShift)
        {
            int shift = 0;
            double mul = 0;

            if (Math.Abs(value) > 1)
            {
                var mulShift = 0;
                mul = C.math.frexp(value, ref mulShift);
                shift = Math.Min(maxShift, maxBits - 1 - mulShift);
                mul = mul * Math.Pow(2, shift + mulShift);
            }
            else if (value == 0)
            {
                mul = shift = 0;
            }
            else
            {
                var mulShift = 0;
                mul = C.math.frexp(value, ref mulShift);
                shift = Math.Min(maxShift + mulShift, maxBits - 1);
                mul = mul * Math.Pow(2, shift);
                shift -= mulShift;
            }

            Debug.Assert(Math.Abs(mul) < Math.Pow(2, maxBits - 1));
            Debug.Assert(shift <= maxShift);
            Debug.Assert(Math.Abs(value - mul * Math.Pow(2, -shift)) <= double.Epsilon);
            return (mul, shift);
        }

        private struct Range
        {
            public double Min;
            public double Max;

            public Range EMA(double alpha, Range range)
            {
                return new Range { Min = alpha * range.Min + (1 - alpha) * Min, Max = alpha * range.Max + (1 - alpha) * Max };
            }

            public (double scale, double bias) GetScaleBias() => GetScaleBias(8);

            public (double scale, double bias) GetScaleBias(int maxBits)
            {
                var scale = ((1 << maxBits) - 1) / (Max - Min);
                var bias = Min * scale;
                return (scale, bias);
            }
        }

        private class QuantizationContext
        {
            public GraphPlanContext PlanContext { get; set; }

            public IReadOnlyList<OutputConnector> Outputs { get; set; }

            public Dictionary<OutputConnector, Range> Distributions { get; } = new Dictionary<OutputConnector, Range>();
        }

        private class ConvertContext
        {
            public QuantizationContext Quantization { get; set; }

            public Dictionary<Layer, bool> ProcessMap = new Dictionary<Layer, bool>();

            public Dictionary<Layer, K210ConvLayerConfig> Layers { get; } = new Dictionary<Layer, K210ConvLayerConfig>();

            public KPUMemoryAllocator MemoryAllocator { get; } = new KPUMemoryAllocator();

            public List<K210ConvLayerConfig> InferenceOrders { get; } = new List<K210ConvLayerConfig>();

            public Dictionary<OutputConnector, KPUMemoryNode> KPUMemoryMap { get; } = new Dictionary<OutputConnector, KPUMemoryNode>();
        }

        private class KPUMemoryAllocator
        {
            private List<KPUMemoryNode> _nodes = new List<KPUMemoryNode>();

            public KPUMemoryAllocator()
            {
                _nodes.Add(new KPUMemoryNode(this) { Start = 0, Size = 2 * 1024 * 1024 / 64 });
            }

            public KPUMemoryNode Allocate(int size)
            {
                var firstFreeIdx = _nodes.FindIndex(o => !o.IsUsed && o.Size >= size);
                if (firstFreeIdx == -1)
                    throw new InvalidOperationException("KPU is out of memory.");
                var firstFree = _nodes[firstFreeIdx];
                if (firstFree.Size == size)
                {
                    firstFree.AddRef();
                    return firstFree;
                }
                else
                {
                    var newNode = new KPUMemoryNode(this)
                    {
                        Start = firstFree.Start,
                        Size = size
                    };
                    newNode.AddRef();

                    firstFree.Size -= size;
                    firstFree.Start += size;
                    _nodes.Insert(firstFreeIdx, newNode);
                    return newNode;
                }
            }

            public void Free(KPUMemoryNode node)
            {
                Debug.Assert(!node.IsUsed);
                var idx = _nodes.IndexOf(node);
                if (idx != 0)
                {
                    var before = _nodes[idx - 1];
                    if (!before.IsUsed)
                    {
                        before.Size += node.Size;
                        _nodes.RemoveAt(idx);
                        idx--;
                        node = _nodes[idx];
                    }
                }
                if (idx != _nodes.Count - 1)
                {
                    var after = _nodes[idx + 1];
                    if (!after.IsUsed)
                    {
                        node.Size += after.Size;
                        _nodes.RemoveAt(idx + 1);
                    }
                }
            }
        }

        private class KPUMemoryNode
        {
            private readonly KPUMemoryAllocator _memoryAllocator;
            private int _useCount;

            public int Start { get; set; }

            public int Size { get; set; }

            public bool IsUsed => _useCount != 0;

            public KPUMemoryNode(KPUMemoryAllocator memoryAllocator)
            {
                _memoryAllocator = memoryAllocator;
            }

            public void AddRef()
            {
                _useCount++;
            }

            public void Release()
            {
                if (--_useCount == 0)
                {
                    _memoryAllocator.Free(this);
                }
            }
        }
    }
}
