// Data/FilmingLocationData.cs
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
// EVENT ID MAPPING (from ContentEventIDMapper.cs):
//   • IDs 1000–1045 are hardcoded monster / player events (static switch).
//     These are pre-populated in EntityIdToLocation below.
//   • IDs outside that range resolve through PropContentDatabase and become
//     ArtifactContentEvent or PropContentEvent at runtime.
//     For artifacts, the identity comes from PropContent.displayName and is
//     looked up via EntityTypeToLocation (the string-name dict).
//
// FORMAT: { "EntityTypeName", "Filmed LocationName" }
//         { ushortId,         "Filmed LocationName" }

using System.Collections.Generic;

namespace ContentWarningArchipelago.Data
{
    public static class FilmingLocationData
    {
        // -----------------------------------------------------------------------
        // Primary lookup: content-event type-name string → AP location name.
        // Used for:
        //   • Artifact display names extracted from ArtifactContentEvent.content.displayName
        //     (e.g. "Ribcage" → "Filmed Ribcage")
        //   • Legacy class-name fallback for any event not in EntityIdToLocation
        // -----------------------------------------------------------------------
        public static readonly Dictionary<string, string> EntityTypeToLocation =
            new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            // ==================== MONSTERS ====================
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
            // Keys match PropContent.displayName values (or Unity Object.name as fallback).
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
        // Direct ID lookup: ushort content-event ID → AP location name.
        //
        // Pre-populated from the hardcoded switch in ContentEventIDMapper.GetContentEventGenerated().
        // Only monster/artifact filming IDs are included; player and misc events
        // (PlayerContentEvent, PlayerDeadContentEvent, etc.) are intentionally omitted.
        //
        // Artifact IDs are NOT listed here because they are dynamically assigned by
        // PropContentDatabase and can change between game versions. Artifacts are
        // identified instead by PropContent.displayName via EntityTypeToLocation.
        // -----------------------------------------------------------------------
        public static readonly Dictionary<ushort, string> EntityIdToLocation =
            new Dictionary<ushort, string>
        {
            // ==================== MONSTERS (IDs from ContentEventIDMapper) ====================
            { 1012, "Filmed Barnacle Ball"  },  // BarnacleBallContentEvent
            { 1002, "Filmed Big Slap"       },  // BigSlapAgroContentEvent
            { 1001, "Filmed Big Slap"       },  // BigSlapPeacefulContentEvent
            { 1043, "Filmed Black Hole Bot" },  // BlackHoleBotContentEvent
            { 1030, "Filmed Bomber"         },  // BombContentEvent
            { 1017, "Filmed Bomber"         },  // BombsContentEvent (multi-bomb variant)
            { 1026, "Filmed Cam Creep"      },  // CamCreepContentEvent
            { 1024, "Filmed Dog"            },  // DogContentEvent
            { 1008, "Filmed Ear"            },  // EarContentEvent
            { 1025, "Filmed Eye Guy"        },  // EyeGuyContentEvent
            { 1041, "Filmed Fire"           },  // FireMonsterContentEvent
            { 1004, "Filmed Flicker"        },  // FlickerContentEvent
            { 1037, "Filmed Harpooner"      },  // HarpoonerContentEvent
            { 1005, "Filmed Jelly"          },  // JelloContentEvent
            { 1006, "Filmed Knifo"          },  // KnifoContentEvent
            { 1018, "Filmed Larva"          },  // LarvaContentEvent
            { 1045, "Filmed Mime"           },  // MimeContentEvent
            { 1009, "Filmed Mouthe"         },  // MouthContentEvent
            { 1042, "Filmed Puffo"          },  // PuffoContentEvent
            { 1035, "Filmed Button Robot"   },  // RobotButtonContentEvent
            { 1010, "Filmed Slurper"        },  // SlurperContentEvent
            { 1040, "Filmed Snail Spawner"  },  // SnailSpawnerContentEvent
            { 1011, "Filmed Snatcho"        },  // SnatchoContentEvent
            { 1019, "Filmed Spider"         },  // SpiderContentEvent
            { 1038, "Filmed Streamer"       },  // StreamerContentEvent
            { 1013, "Filmed Whisk"          },  // ToolkitWhiskContentEvent
            { 1036, "Filmed Arms"           },  // WalloContentEvent
            { 1007, "Filmed Weeping"        },  // WeepingContentEvent
            { 1014, "Filmed Weeping"        },  // WeepingContentEventCaptured
            { 1015, "Filmed Weeping"        },  // WeepingContentEventFail
            { 1016, "Filmed Weeping"        },  // WeepingContentEventSuccess
            { 1039, "Filmed Worm"           },  // WormContentEvent
            { 1003, "Filmed Zombe"          },  // ZombieContentEvent
        };

        /// <summary>
        /// Tries to resolve an AP location name from a string entity type identifier.
        /// Used for artifact displayName lookups and class-name fallback.
        /// Returns null if no match found.
        /// </summary>
        public static string? TryGetLocationByTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            return EntityTypeToLocation.TryGetValue(typeName, out var loc) ? loc : null;
        }

        /// <summary>
        /// Tries to resolve an AP location name from a ushort content-event ID.
        /// Covers the hardcoded monster IDs from ContentEventIDMapper.
        /// Returns null if no match found (e.g. dynamic artifact IDs).
        /// </summary>
        public static string? TryGetLocationById(ushort typeId)
        {
            return EntityIdToLocation.TryGetValue(typeId, out var loc) ? loc : null;
        }
    }
}
