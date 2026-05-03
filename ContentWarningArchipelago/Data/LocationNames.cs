// Data/LocationNames.cs — mirrors cw-apworld/ap_world/names/location_names.py as C# constants.
// Base location ID: 98765000  (location_base_id in locations.py)

using System.Globalization;

namespace ContentWarningArchipelago.Data
{
    public static class LocationNames
    {
        // ---- Extractions ----
        public const string AnyExtraction           = "Any Extraction";
        public const string ExtractedFootagePrefix  = "Extracted Footage on Day ";  // + day number, 1..63

        // ---- Quotas ----
        public const string MetQuotaPrefix = "Met Quota ";   // + quota number, 1..21

        // ---- View Milestones ----
        // Helper that mirrors Python's lname.reached_total_views(total).
        // Names use comma thousands separators (matching apworld f"{total:,}").
        public static string ReachedTotalViews(int total)
            => $"Reached {total.ToString("N0", CultureInfo.InvariantCulture)} Total Views";

        // ---- Sponsorships ----
        // Per-completion trigger; counter persists across run loss/restart.
        public const string CompletedSponsorshipPrefix = "Completed Sponsorship ";  // + N, 1..20

        // ---- Viral Sensation event ----
        public const string ViralSensationAchieved = "Viral Sensation Achieved";

        // ---- Victory ----
        public const string Victory = "Victory";
    }
}
