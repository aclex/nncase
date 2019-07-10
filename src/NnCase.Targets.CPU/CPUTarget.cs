﻿using System;
using System.Collections.Generic;
using System.Text;
using NnCase.Evaluation;
using NnCase.IR;
using NnCase.Targets.CPU.Evaluation.Operators;
using NnCase.Targets.CPU.Transforms;
using NnCase.Transforms;

namespace NnCase.Targets.CPU
{
    public class CPUTarget : Target
    {
        protected override void AddOptimize2Transforms(List<Transform> transforms)
        {
            transforms.AddRange(new Transform[]
            {
                new CPUDepthwiseConv2DTransform(),
                new CPUConv2DTransform(),
                new CPUReduceWindow2DTransform()
            });
        }

        public override void RegisterEvaluators(EvaluatorRegistry registry)
        {
            CPUEvaulators.Register(registry);
        }
    }
}