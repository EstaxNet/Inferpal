# Fixes a recognizer's input to [1, 3, 48, WIDTH] and folds every shape computation (onnxsim, no Gemm fusion), then turns
# Constant nodes into initializers — so the managed interpreter runs it. Usage: simplify.py <in.onnx> <out.onnx> <width>
import sys, onnx, onnxsim
from onnx import numpy_helper

src, dst, width = sys.argv[1], sys.argv[2], int(sys.argv[3])
m = onnx.load(src)
# An old export (opset 9: Slice bounds as attributes) brought to the opset the interpreter reads (bounds as inputs).
if m.opset_import[0].version < 13:
    from onnx import version_converter
    m = version_converter.convert_version(m, 14)
name = m.graph.input[0].name
simplified, ok = onnxsim.simplify(m, overwrite_input_shapes={name: [1, 3, 48, width]},
                                  skipped_optimizers=["fuse_matmul_add_bias_into_gemm", "fuse_bn_into_conv"])
assert ok
g = simplified.graph
kept = []
for n in g.node:
    if n.op_type == "Constant":
        value = next(a for a in n.attribute if a.name == "value")
        tensor = numpy_helper.to_array(value.t)
        g.initializer.append(numpy_helper.from_array(tensor, n.output[0]))
    elif n.op_type == "Gemm":
        # Gemm (a Linear layer) = MatMul then Add: Gemm is not among the interpreter's operators.
        at = {a.name: (a.i if a.type == onnx.AttributeProto.INT else a.f) for a in n.attribute}
        assert at.get("alpha", 1.0) == 1.0 and at.get("beta", 1.0) == 1.0 and not at.get("transA", 0), at
        inits = {i.name: i for i in g.initializer}
        weight = n.input[1]
        if at.get("transB", 0):
            w = numpy_helper.to_array(inits[weight]).T.copy()
            weight = weight + "_T"
            g.initializer.append(numpy_helper.from_array(w, weight))
        product = n.output[0] + "_mm" if len(n.input) > 2 else n.output[0]
        kept.append(onnx.helper.make_node("MatMul", [n.input[0], weight], [product]))
        if len(n.input) > 2:
            kept.append(onnx.helper.make_node("Add", [product, n.input[2]], [n.output[0]]))
    else:
        kept.append(n)
del g.node[:]
g.node.extend(kept)
onnx.checker.check_model(simplified)
onnx.save(simplified, dst)
print("ops", sorted({n.op_type for n in g.node}))
