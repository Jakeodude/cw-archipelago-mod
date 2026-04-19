// Data/MonsterFilmingData.cs
//
// Maps content-event entity type IDs (as used by ContentPolling / ContentBuffer internally)
// to Archipelago "Filmed X" location names.
//
// HOW CONTENT EVENTS WORK (from VideoCamera.cs / VideoChunk.cs reference):
//   1. While recording, ContentPolling.StartPolling() scans the scene each frame.
//   2. When a filmable entity (monster, artifact) is on camera, it generates a
//      ContentEventFrame that identifies the entity by some type identifier.
//   3. These frames are pushed into a ContentBuffer (contentBuffer.PushFrame).
//   4. ContentEvaluator.EvaluateRecording() scores the recorded ContentBuffer.
//
// ENTITY ID DISCOVERY:
//   The entity type values below are the string representations of the types used
//   by the game's content evaluation system.  Content Warning identifies filmed
//   entities via a "ContentType" or similar enum/ID.  The values here use the
//   most likely class/type names discovered from the game reference assembly scan.
//   They should be verified against the live game by enabling debug logging in
//   ContentEvaluatorPatch and checking what type identifiers appear.
//
//   The patch in ItemPickupPatch.cs logs every unique type string it encounters
//   so you can identify missing monsters and add them here.
//
// FORMAT: { "EntityTypeName", "Filmed LocationName" }

using System.Collections.Generic;

namespace ContentWarningArchipelago.Data
{
    public static class MonsterFilmingData
    {
        // -----------------------------------------------------------------------
        // Primary lookup: content-event type-name string → AP location name.
        // The key is whatever ToString() or class-name string the content event
        // exposes as its type identifier.
        // -----------------------------------------------------------------------
        public static readonly Dictionary<string, string> EntityTypeToLocation =
            new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            // ==================== MONSTERS ====================
            // Key = ContentType / entity class name as reported at runtime.
            // These must match what ContentEvaluatorPatch logs as "Unknown entity type".
            { "Slurper",         "Filmed Slurper"       },
            { "Zombe",           "Filmed Zombe"         },
            { "Worm",            "Filmed Worm"          },
            { "Mouthe",          "Filmed Mouthe"        },
            { "Flicker",         "Filmed Flicker"       },
            { "CamCreep",        "Filmed Cam Creep"     },
            { "Cam Creep",       "Filmed Cam Creep"     },
            { "Infiltrator",     "Filmed Infiltrator"   },
            { "ButtonRobot",     "Filmed Button Robot"  },
            { "Button Robot",    "Filmed Button Robot"  },
            { "Puffo",           "Filmed Puffo"         },
            { "BlackHoleBot",    "Filmed Black Hole Bot"},
            { "Black Hole Bot",  "Filmed Black Hole Bot"},
            { "Snatcho",         "Filmed Snatcho"       },
            { "Whisk",           "Filmed Whisk"         },
            { "Spider",          "Filmed Spider"        },
            { "Ear",             "Filmed Ear"           },
            { "Jelly",           "Filmed Jelly"         },
            { "Weeping",         "Filmed Weeping"       },
            { "Bomber",          "Filmed Bomber"        },
            { "Dog",             "Filmed Dog"           },
            { "RobotDog",        "Filmed Dog"           },
            { "Robot Dog",       "Filmed Dog"           },
            { "EyeGuy",          "Filmed Eye Guy"       },
            { "Eye Guy",         "Filmed Eye Guy"       },
            { "Fire",            "Filmed Fire"          },
            { "Knifo",           "Filmed Knifo"         },
            { "Larva",           "Filmed Larva"         },
            { "GrabberSnake",    "Filmed Larva"         }, // alternate name
            { "Arms",            "Filmed Arms"          },
            { "Harpooner",       "Filmed Harpooner"     },
            { "Mime",            "Filmed Mime"          },
            { "BarnackleBall",   "Filmed Barnacle Ball" },
            { "Barnacle Ball",   "Filmed Barnacle Ball" },
            { "SnailSpawner",    "Filmed Snail Spawner" },
            { "Snail Spawner",   "Filmed Snail Spawner" },
            { "BigSlap",         "Filmed Big Slap"      },
            { "Big Slap",        "Filmed Big Slap"      },
            { "Streamer",        "Filmed Streamer"      },
            { "UltraKnifo",      "Filmed Ultra Knifo"   },
            { "Ultra Knifo",     "Filmed Ultra Knifo"   },

            // ==================== ARTIFACTS ====================
            { "Ribcage",                "Filmed Ribcage"               },
            { "Skull",                  "Filmed Skull"                 },
            { "Spine",                  "Filmed Spine"                 },
            { "Bone",                   "Filmed Bone"                  },
            { "BrainOnAStick",          "Filmed Brain on a Stick"      },
            { "Brain on a Stick",       "Filmed Brain on a Stick"      },
            { "Radio",                  "Filmed Radio"                 },
            { "Shroom",                 "Filmed Shroom"                },
            { "AnimalStatues",          "Filmed Animal Statues"        },
            { "Animal Statues",         "Filmed Animal Statues"        },
            { "RadioactiveContainer",   "Filmed Radioactive Container" },
            { "Radioactive Container",  "Filmed Radioactive Container" },
            { "OldPainting",            "Filmed Old Painting"          },
            { "Old Painting",           "Filmed Old Painting"          },
            { "Chorby",                 "Filmed Chorby"                },
            { "Apple",                  "Filmed Apple"                 },
            { "ReporterMic",            "Filmed Reporter Mic"          },
            { "Reporter Mic",           "Filmed Reporter Mic"          },
        };

        // -----------------------------------------------------------------------
        // Fallback lookup by numeric content-type ID.
        // Populate this once actual IDs are known from in-game debug logging.
        // The patch logs "Unknown entity type: <N>" for any ID not in this table.
        // -----------------------------------------------------------------------
        public static readonly Dictionary<int, string> EntityIdToLocation =
            new Dictionary<int, string>
        {
            // Placeholder — fill these in after observing debug logs.
            // Example: { 1, "Filmed Slurper" },
        };

        /// <summary>
        /// Tries to resolve an AP location name from a string entity type identifier.
        /// Returns null if no match found.
        /// </summary>
        public static string? TryGetLocationByTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            return EntityTypeToLocation.TryGetValue(typeName, out var loc) ? loc : null;
        }

        /// <summary>
        /// Tries to resolve an AP location name from a numeric entity type ID.
        /// Returns null if no match found.
        /// </summary>
        public static string? TryGetLocationById(int typeId)
        {
            return EntityIdToLocation.TryGetValue(typeId, out var loc) ? loc : null;
        }
    }
}
