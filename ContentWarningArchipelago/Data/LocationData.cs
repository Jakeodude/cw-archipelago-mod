// Data/LocationData.cs
// Maps Archipelago location IDs ↔ location names, matching locations.py in the apworld.
// Base ID: 98765000 (location_base_id)   Offset = location_id_offset from the table.

using System.Collections.Generic;

namespace ContentWarningArchipelago.Data
{
    public static class LocationData
    {
        public const long BaseId = 98765000L;

        // Pre-built lookup tables (populated once in Init).
        public static Dictionary<string, long> NameToId { get; private set; } = new();
        public static Dictionary<long, string> IdToName { get; private set; } = new();

        public static void Init()
        {
            // ==================== BASIC EXTRACTION & DAY CHECKS ====================
            Register(LocationNames.AnyExtraction, 0);

            for (int day = 1; day <= 15; day++)
                Register(LocationNames.ExtractedFootagePrefix + day, day);

            // ==================== QUOTA CHECKS ====================
            // offsets 100–109
            for (int q = 1; q <= 10; q++)
                Register(LocationNames.MetQuotaPrefix + q, 100 + (q - 1));

            // ==================== VIEW MILESTONES (offsets 200–216) ====================
            Register(LocationNames.Reached1k,   200);
            Register(LocationNames.Reached2k,   201);
            Register(LocationNames.Reached3k,   202);
            Register(LocationNames.Reached13k,  203);
            Register(LocationNames.Reached26k,  204);
            Register(LocationNames.Reached39k,  205);
            Register(LocationNames.Reached43k,  206);
            Register(LocationNames.Reached85k,  207);
            Register(LocationNames.Reached128k, 208);
            Register(LocationNames.Reached150k, 209);
            Register(LocationNames.Reached220k, 210);
            Register(LocationNames.Reached325k, 211);
            Register(LocationNames.Reached375k, 212);
            Register(LocationNames.Reached430k, 213);
            Register(LocationNames.Reached645k, 214);
            Register(LocationNames.Reached850k, 215);
            Register(LocationNames.Reached1m,   216);

            // ==================== MONSTER FILMING (offsets 300–329) ====================
            Register("Filmed Slurper",       300);
            Register("Filmed Zombe",         301);
            Register("Filmed Worm",          302);
            Register("Filmed Mouthe",        303);
            Register("Filmed Flicker",       304);
            Register("Filmed Cam Creep",     305);
            Register("Filmed Infiltrator",   306);
            Register("Filmed Button Robot",  307);
            Register("Filmed Puffo",         308);
            Register("Filmed Black Hole Bot",309);
            Register("Filmed Snatcho",       310);
            Register("Filmed Whisk",         311);
            Register("Filmed Spider",        312);
            Register("Filmed Ear",           313);
            Register("Filmed Jelly",         314);
            Register("Filmed Weeping",       315);
            Register("Filmed Bomber",        316);
            Register("Filmed Dog",           317);
            Register("Filmed Eye Guy",       318);
            Register("Filmed Fire",          319);
            Register("Filmed Knifo",         320);
            Register("Filmed Larva",         321);
            Register("Filmed Arms",          322);
            Register("Filmed Harpooner",     323);
            Register("Filmed Mime",          324);
            Register("Filmed Barnacle Ball", 325);
            Register("Filmed Snail Spawner", 326);
            Register("Filmed Big Slap",      327);
            Register("Filmed Streamer",      328);
            Register("Filmed Ultra Knifo",   329);

            // ==================== ARTIFACT FILMING (offsets 400–412) ====================
            Register("Filmed Ribcage",                400);
            Register("Filmed Skull",                  401);
            Register("Filmed Spine",                  402);
            Register("Filmed Bone",                   403);
            Register("Filmed Brain on a Stick",       404);
            Register("Filmed Radio",                  405);
            Register("Filmed Shroom",                 406);
            Register("Filmed Animal Statues",         407);
            Register("Filmed Radioactive Container",  408);
            Register("Filmed Old Painting",           409);
            Register("Filmed Chorby",                 410);
            Register("Filmed Apple",                  411);
            Register("Filmed Reporter Mic",           412);

            // ==================== STORE PURCHASES (offsets 500–515) ====================
            Register("Bought Old Flashlight",      500);
            Register("Bought Flare",               501);
            Register("Bought Modern Flashlight",   502);
            Register("Bought Long Flashlight",     503);
            Register("Bought Modern Flashlight Pro",504);
            Register("Bought Long Flashlight Pro", 505);
            Register("Bought Hugger",              506);
            Register("Bought Defibrillator",       507);
            Register("Bought Reporter Mic",        508);
            Register("Bought Boom Mic",            509);
            Register("Bought Clapper",             510);
            Register("Bought Sound Player",        511);
            Register("Bought Goo Ball",            512);
            Register("Bought Rescue Hook",         513);
            Register("Bought Shock Stick",         514);
            Register("Bought Sketch Pad",          515);

            // ==================== EMOTES (offsets 550–565) ====================
            Register("Bought Applause",           550);
            Register("Bought Workout 1",          551);
            Register("Bought Confused",           552);
            Register("Bought Dance 103",          553);
            Register("Bought Dance 102",          554);
            Register("Bought Dance 101",          555);
            Register("Bought Backflip",           556);
            Register("Bought Gymnastics",         557);
            Register("Bought Caring",             558);
            Register("Bought Ancient Gestures 3", 559);
            Register("Bought Ancient Gestures 2", 560);
            Register("Bought Yoga",               561);
            Register("Bought Workout 2",          562);
            Register("Bought Thumbnail 1",        563);
            Register("Bought Thumbnail 2",        564);
            Register("Bought Ancient Gestures 1", 565);

            // ==================== EMOTE EXTRAS (offset 570) ====================
            Register("Bought Party Popper", 570);

            // ==================== HATS (offsets 600–630) ====================
            Register("Bought Balaclava",     600);
            Register("Bought Beanie",        601);
            Register("Bought Bucket Hat",    602);
            Register("Bought Cat Ears",      603);
            Register("Bought Chefs Hat",     604);
            Register("Bought Floppy Hat",    605);
            Register("Bought Homburg",       606);
            Register("Bought Curly Hair",    607);
            Register("Bought Bowler Hat",    608);
            Register("Bought Cap",           609);
            Register("Bought Propeller Hat", 610);
            Register("Bought Clown Hair",    611);
            Register("Bought Cowboy Hat",    612);
            Register("Bought Crown",         613);
            Register("Bought Halo",          614);
            Register("Bought Horns",         615);
            Register("Bought Hotdog Hat",    616);
            Register("Bought Jesters Hat",   617);
            Register("Bought Ghost Hat",     618);
            Register("Bought Milk Hat",      619);
            Register("Bought News Cap",      620);
            Register("Bought Pirate Hat",    621);
            Register("Bought Sports Helmet", 622);
            Register("Bought Tooop Hat",     623);
            Register("Bought Top Hat",       624);
            Register("Bought Party Hat",     625);
            Register("Bought Shroom Hat",    626);
            Register("Bought Ushanka",       627);
            Register("Bought Witch Hat",     628);
            Register("Bought Hard Hat",      629);
            Register("Bought Savannah Hair", 630);

            // ==================== SPONSORSHIPS (offsets 700–706) ====================
            Register(LocationNames.AcceptedSponsorshipPrefix  + "1",         700);
            Register(LocationNames.AcceptedSponsorshipPrefix  + "2",         701);
            Register(LocationNames.AcceptedSponsorshipPrefix  + "3",         702);
            Register(LocationNames.CompletedSponsorshipPrefix + "Easy",      703);
            Register(LocationNames.CompletedSponsorshipPrefix + "Medium",    704);
            Register(LocationNames.CompletedSponsorshipPrefix + "Hard",      705);
            Register(LocationNames.CompletedSponsorshipPrefix + "Very Hard", 706);
        }

        // ------------------------------------------------------------------
        private static void Register(string name, int offset)
        {
            long id = BaseId + offset;
            if (!NameToId.ContainsKey(name))
            {
                NameToId[name] = id;
                IdToName[id]   = name;
            }
        }

        // ------------------------------------------------------------------
        /// <summary>Returns the full AP location ID for the given location name, or -1.</summary>
        public static long GetId(string locationName)
            => NameToId.TryGetValue(locationName, out var id) ? id : -1L;

        /// <summary>Returns the location name for the given AP ID, or an error string.</summary>
        public static string GetName(long id)
            => IdToName.TryGetValue(id, out var name) ? name : $"Unknown Location ({id})";

        /// <summary>Strips the base ID so you can compare raw offsets.</summary>
        public static long RemoveBaseId(long id) => id - BaseId;
    }
}
