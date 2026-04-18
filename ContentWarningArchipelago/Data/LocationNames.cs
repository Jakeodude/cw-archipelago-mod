// Data/LocationNames.cs — mirrors cw-apworld/ap_world/names/location_names.py as C# constants.
// Base location ID: 98765000  (location_base_id in locations.py)

namespace ContentWarningArchipelago.Data
{
    public static class LocationNames
    {
        // ---- Extractions ----
        public const string AnyExtraction           = "Any Extraction";
        public const string ExtractedFootagePrefix  = "Extracted Footage on Day ";  // + day number

        // ---- Quotas ----
        public const string MetQuotaPrefix = "Met Quota ";   // + quota number

        // ---- View Milestones ----
        public const string Reached1k   = "Reached 1,000 Total Views";
        public const string Reached2k   = "Reached 2,000 Total Views";
        public const string Reached3k   = "Reached 3,000 Total Views";
        public const string Reached13k  = "Reached 13,000 Total Views";
        public const string Reached26k  = "Reached 26,000 Total Views";
        public const string Reached39k  = "Reached 39,000 Total Views";
        public const string Reached43k  = "Reached 43,000 Total Views";
        public const string Reached85k  = "Reached 85,000 Total Views";
        public const string Reached128k = "Reached 128,000 Total Views";
        public const string Reached150k = "Reached 150,000 Total Views";
        public const string Reached220k = "Reached 220,000 Total Views";
        public const string Reached325k = "Reached 325,000 Total Views";
        public const string Reached375k = "Reached 375,000 Total Views";
        public const string Reached430k = "Reached 430,000 Total Views";
        public const string Reached645k = "Reached 645,000 Total Views";
        public const string Reached850k = "Reached 850,000 Total Views";
        public const string Reached1m   = "Reached 1,000,000 Total Views";

        // ---- Sponsorships ----
        public const string AcceptedSponsorshipPrefix  = "Accepted Sponsorship ";   // + "1", "2", "3"
        public const string CompletedSponsorshipPrefix = "Completed Sponsorship ";  // + difficulty name

        // ---- Victory ----
        public const string Victory = "Victory";
    }
}
