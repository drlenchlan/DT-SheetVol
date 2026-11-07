# Planning

Owner: C1 (HANDOFF.md section 10). Break-spec parser, segment planning, identity rules, overlap detection (6.3).

Rev 3: `Planner.Plan` drops repeated and straight-through vertices from every band piece (`BandRing.DropStraightVertices`, station tolerance) before the partition check measures it. MEASURED 2026-11-06 on T25: Civil 3D's GetBoundedVolumes gave fill 708,942.2 on the two-segment band with vertices at station 500 on both lateral edges, 828,000 on the same polygon without them (`BandRingTests`). Segment boundaries keep their break vertices.
