// Data/ViewMilestones.cs
// Canonical lifetime-views milestone table — mirrors VIEW_MILESTONES in
// cw-apworld/locations.py.  One milestone per in-game day (1..63).
// Used by:
//   • LocationData.Init — registers a Reached-N-Views location per milestone.
//   • ViewsTracker      — tests the running lifetime-views total against
//                         every milestone to fire crossed checks.

namespace ContentWarningArchipelago.Data
{
    public static class ViewMilestones
    {
        /// <summary>(day, lifetime_total_views) for each in-game day 1..63.</summary>
        public static readonly (int day, int total)[] Table = new[]
        {
            (1,        1_000), ( 2,        2_000), ( 3,        3_000),
            (4,       16_000), ( 5,       29_000), ( 6,       42_000),
            (7,       84_667), ( 8,      127_333), ( 9,      170_000),
            (10,     278_333), (11,      386_667), (12,      495_000),
            (13,     709_667), (14,      924_333), (15,    1_139_000),
            (16,   1_505_667), (17,    1_872_333), (18,    2_239_000),
            (19,   2_839_000), (20,    3_439_000), (21,    4_039_000),
            (22,   4_639_000), (23,    5_239_000), (24,    5_839_000),
            (25,   6_472_333), (26,    7_105_667), (27,    7_739_000),
            (28,   8_372_333), (29,    9_005_667), (30,    9_639_000),
            (31,  10_305_667), (32,   10_972_333), (33,   11_639_000),
            (34,  12_305_667), (35,   12_972_333), (36,   13_639_000),
            (37,  14_305_667), (38,   14_972_333), (39,   15_639_000),
            (40,  16_339_000), (41,   17_039_000), (42,   17_739_000),
            (43,  18_439_000), (44,   19_139_000), (45,   19_839_000),
            (46,  20_572_333), (47,   21_305_667), (48,   22_039_000),
            (49,  22_772_333), (50,   23_505_667), (51,   24_239_000),
            (52,  25_005_667), (53,   25_772_333), (54,   26_539_000),
            (55,  27_305_667), (56,   28_072_333), (57,   28_839_000),
            (58,  29_639_000), (59,   30_439_000), (60,   31_239_000),
            (61,  32_072_333), (62,   32_905_667), (63,   33_739_000),
        };

        /// <summary>Highest milestone in the table — also the maximum value of
        /// <c>views_goal_target</c> in apworld options.py.</summary>
        public const int MaxLifetimeViews = 33_739_000;

        /// <summary>Threshold for the per-quota "Viral Sensation Achieved" check.</summary>
        public const int ViralSensationThreshold = 1_000_000;
    }
}
