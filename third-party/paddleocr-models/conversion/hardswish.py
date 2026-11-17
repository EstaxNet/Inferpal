# Rewrites an ONNX model's HardSwish and HardSigmoid nodes with base operators the managed interpreter runs:
# HardSigmoid(x; alpha, beta) = Clip(alpha*x + beta, 0, 1);  HardSwish(x) = x * Clip(x/6 + 0.5, 0, 1).
# Usage: hardswish.py <in.onnx> <out.onnx> — prints the op types of the result.
import sys, onnx
from onnx import helper, numpy_helper
import numpy as np

src, dst = sys.argv[1], sys.argv[2]
m = onnx.load(src)
g = m.graph
consts = {}
def const(value):
    name = f"__c_{len(consts)}"
    if value not in consts.values():
        g.initializer.append(numpy_helper.from_array(np.array(value, dtype=np.float32), name))
        consts[name] = value
        return name
    return next(k for k, v in consts.items() if v == value)

out_nodes = []
for i, n in enumerate(g.node):
    if n.op_type in ("HardSwish", "HardSigmoid"):
        alpha, beta = 1 / 6, 0.5
        for a in n.attribute:
            if a.name == "alpha": alpha = a.f
            if a.name == "beta": beta = a.f
        x, y = n.input[0], n.output[0]
        p = f"__hs{i}"
        out_nodes += [
            helper.make_node("Mul", [x, const(alpha)], [p + "_m"]),
            helper.make_node("Add", [p + "_m", const(beta)], [p + "_a"]),
            # clip(v, 0, 1) = Relu(v) - Relu(v - 1): Clip is not among the interpreter's operators.
            helper.make_node("Relu", [p + "_a"], [p + "_r0"]),
            helper.make_node("Sub", [p + "_a", const(1.0)], [p + "_s"]),
            helper.make_node("Relu", [p + "_s"], [p + "_r1"]),
            helper.make_node("Sub", [p + "_r0", p + "_r1"], [p + "_c" if n.op_type == "HardSwish" else y]),
        ]
        if n.op_type == "HardSwish":
            out_nodes.append(helper.make_node("Mul", [x, p + "_c"], [y]))
    else:
        out_nodes.append(n)
del g.node[:]
g.node.extend(out_nodes)
onnx.checker.check_model(m)
onnx.save(m, dst)
print(sorted({n.op_type for n in g.node}))
