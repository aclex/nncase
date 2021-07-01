/* Copyright 2019-2021 Canaan Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
#pragma once
#include "../node.h"
#include <xtensor/xtensor.hpp>

namespace nncase::ir
{
class NNCASE_API pad : public node
{
public:
    DEFINE_NODE_OPCODE(op_pad);

    input_connector &input() { return input_at(0); }
    output_connector &output() { return output_at(0); }

    const xt::svector<padding> &paddings() const noexcept { return paddings_; }
    pad_mode_t pad_mode() const noexcept { return pad_mode_; }
    const scalar &pad_value() const noexcept { return pad_value_; }

    pad(datatype_t type, shape_t input_shape, xt::svector<padding> paddings, pad_mode_t pad_mode, scalar pad_value);

protected:
    bool properties_equal(node &other) const override;

private:
    xt::svector<padding> paddings_;
    pad_mode_t pad_mode_;
    scalar pad_value_;
};
}
