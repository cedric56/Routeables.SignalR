import 'types.dart';

/// Applies a patch to a state without changing it: the maps and the lists on the path are copied, what did not change is shared.
State applyPatch(State state, Patch patch) => asMap(_update(state, patch.path, 0, patch));

Object? _update(Object? node, List<Object> path, int depth, Patch patch) {
  if (depth == path.length) {
    if (patch.op == 'set') return patch.value;
    final list = node is List ? List<Object?>.of(node) : <Object?>[];
    final index = patch.index ?? 0;
    final remove = patch.remove ?? 0;
    list.removeRange(index, index + remove);
    list.insertAll(index, patch.value is List ? patch.value as List : const <Object?>[]);
    return list;
  }

  final key = path[depth];
  if (node is List) {
    final copy = List<Object?>.of(node);
    final index = key is int ? key : int.parse(key.toString());
    copy[index] = _update(node[index], path, depth + 1, patch);
    return copy;
  }
  final map = node is Map ? asMap(node) : <String, Object?>{};
  final name = key.toString();
  map[name] = _update(map[name], path, depth + 1, patch);
  return map;
}
