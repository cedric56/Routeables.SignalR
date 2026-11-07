import 'package:flutter/material.dart';
import 'package:routeables_client/routeables_client.dart' as rt;

import 'app_state.dart';
import 'page_view.dart';

/// Follows what a target shows (a region, `$name`) while the widget is there. The server sends it while somebody follows it.
mixin FollowsTarget<T extends StatefulWidget> on State<T> {
  rt.Page? followed;
  void Function()? _stop;

  /// The target to follow.
  String get target;

  void follow() {
    _stop?.call();
    _stop = app.client.subscribeTo(target, (page) {
      if (mounted) setState(() => followed = page);
    });
  }

  @override
  void initState() {
    super.initState();
    follow();
  }

  @override
  void dispose() {
    _stop?.call();
    super.dispose();
  }
}

/// The zone of a region: it shows what the router of the region displays. Nothing assigns a page to it; the page is chosen by its
/// view model. A page that arrives comes in with the transition of the zone (the key is the number of the page).
class Region extends StatefulWidget {
  const Region({super.key, required this.name, this.transition = 'None'});

  final String name;

  /// The transition the pages come in with (None, Fade, Slide…).
  final String transition;

  @override
  State<Region> createState() => _RegionState();
}

class _RegionState extends State<Region> with FollowsTarget<Region> {
  @override
  String get target => widget.name;

  @override
  void didUpdateWidget(Region old) {
    super.didUpdateWidget(old);
    if (old.name != widget.name) follow();
  }

  @override
  Widget build(BuildContext context) {
    final page = followed;
    final child = page == null
        ? const SizedBox.shrink(key: ValueKey(0))
        : KeyedSubtree(key: ValueKey(page.sequence), child: RouteablePageView(page: page, target: widget.name));
    final transition = widget.transition.toLowerCase();
    if (transition == 'none') return child;
    return AnimatedSwitcher(
      duration: const Duration(milliseconds: 260),
      transitionBuilder: (child, animation) => transition.contains('slide')
          ? SlideTransition(position: Tween(begin: const Offset(0.15, 0), end: Offset.zero).animate(animation), child: FadeTransition(opacity: animation, child: child))
          : FadeTransition(opacity: animation, child: child),
      child: child,
    );
  }
}
