using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using TombLib.LevelData.SectorEnums;

namespace TombLib.LevelData.Compilers.TombEngine
{
    /*
     * =============================================================================================
     * TOMBENGINE PATHFINDING SYSTEM - BOX AND OVERLAP GENERATION
     * =============================================================================================
     *
     * OVERVIEW:
     * ---------
     * The pathfinding system divides walkable floor areas into rectangular "boxes".
     * Boxes are connected via "overlaps" which define how enemies can move between them.
     * The game engine uses this pre-computed data for efficient AI navigation.
     *
     * DATA STRUCTURES:
     * ----------------
     * - Box: A rectangular walkable floor region with uniform height
     * - Overlap: A connection between two boxes with movement capability flags
     * - Zone: Groups of boxes reachable by a specific enemy type (computed in Pathfinding.cs)
     *
     * COORDINATE SYSTEM:
     * ------------------
     * TombEditor uses X,Z horizontal coordinates with Z=0 at bottom (increasing upward).
     * Vertical (height) uses Y axis with negative values being higher (TR convention).
     *
     * ALGORITHM OVERVIEW:
     * -------------------
     * 1. For each sector in each room, try to create/expand a box (spiral expansion)
     * 2. Deduplicate boxes (same bounds + height + water = same box)
     * 3. For each pair of boxes, check if they overlap/connect
     * 4. Store overlap connections with capability flags (jump, monkey swing)
     * =============================================================================================
     */

    /// <summary>
    /// Auxiliary box structure used during pathfinding generation.
    /// This is the internal working format before conversion to TombEngineBox.
    /// </summary>
    public class dec_TombEngine_box_aux
    {
        // =========================================================================================
        // BOUNDS (in world sector coordinates)
        // =========================================================================================
        public int Zmin;           // Bottom edge (minimum Z, corresponds to "top" in original)
        public int Zmax;           // Top edge (maximum Z, corresponds to "bottom" in original)
        public int Xmin;           // Left edge (minimum X)
        public int Xmax;           // Right edge (maximum X)

        // =========================================================================================
        // FLOOR PROPERTIES
        // =========================================================================================
        public int Height;         // Average floor height (room.Position.Y + average of 4 corners)
        public int MonkeyCeiling;  // Real ceiling height for monkey swing sectors.
        public int OverlapIndex;   // Index into overlap array (-1 if no overlaps)
        public bool Slope;         // True if floor is too steep for most enemies (gradient >= 3 clicks)

        // =========================================================================================
        // BOX FLAGS
        // =========================================================================================
        public bool Splitter;      // Box is a "splitter" (1x1 box at SPLITTER)
        public bool NotWalkableBox;// Box is marked as not walkable
        public bool Monkey;        // Box has monkey swing ceiling (MONKEY)
        public bool Jump;          // Box requires jumping to reach (set during overlap check)
        public bool Water;         // Box is in a water room
        public bool Shallow;       // Box is in shallow water (water depth <= 1 click)

        // =========================================================================================
        // FLIP STATE FLAGS
        // =========================================================================================
        // These flags track which room states the box exists in.
        // A box in a non-flipped room has both flags set (exists in both states).
        // A box in a flip pair has only one flag set.
        public bool Unflipped;     // Box exists in normal (unflipped) room state
        public bool Flipped;       // Box exists in alternate (flipped) room state

        // =========================================================================================
        // ROOM REFERENCE
        // =========================================================================================
        public Room Room;          // The room this box belongs to (after floor portal traversal)
    }

    /// <summary>
    /// Partial class containing box and overlap generation code.
    /// </summary>
    public sealed partial class LevelCompilerTombEngine
    {
        public class BoxFlags
        {
            public const int Water		= 0x0200;
            public const int Shallow	= 0x0400;
            public const int Blocked	= 0x4000;
            public const int Splitter	= 0x8000;
        }

        public class OverlapFlags
        {
            // FLIP-STATE VALIDITY (runtime filter).
            //
            // The compiler runs two overlap passes (Pass 1 = unflipped geometry,
            // Pass 2 = flipped geometry). For pairs whose adjacency check yields
            // the same result in both passes, only ONE physical overlap entry is
            // emitted but it gets BOTH flags. For pairs where alt geometry adds
            // or removes a wall (= overlap valid in one state only), the entry
            // carries only the matching flag.
            //
            // Runtime BFS (CanExpandToBox) reads FlipStatus and rejects entries
            // missing the matching validity flag. Prevents the classic flipmap
            // bug: a Pass 1 base-geometry overlap incorrectly used in alt state
            // routes BFS through a wall that exists only in alt.
            public const int UnflippedValid = 0x0001;
            public const int FlippedValid   = 0x0002;

            public const int Jump = 0x0800;
            public const int Monkey = 0x2000;
            public const int AmphibiousTraversable = 0x4000;
            public const int End = 0x8000;
        }

        // =========================================================================================
        // GLOBAL STATE VARIABLES
        // =========================================================================================
        // These mirror global variables from the original C code.
        // They are used to pass state between functions without explicit parameters.

        /// <summary>
        /// Flag set by Dec_GetHeight when a SPLITTER is encountered.
        /// When true, the current sector must become a 1x1 "splitter" box.
        /// Splitter boxes break up larger boxes to allow fine-grained AI control.
        /// </summary>
        private bool dec_splitter;

        /// <summary>
        /// Flag for shallow water detection.
        /// Set to false when shallow water is detected (water depth &lt;= 1 click).
        /// Shallow water is treated as dry land for pathfinding purposes.
        /// </summary>
        private bool dec_checkUnderwater = true;

        /// <summary>
        /// Flag set by Dec_GetHeight when shallow water is detected.
        /// True when water depth &lt;= 1 click and there's air above.
        /// Used to set the SHALLOW flag (0x0400) on the box.
        /// </summary>
        private bool dec_shallowWater;

        /// <summary>
        /// Flag set by Dec_GetHeight when a MONKEY is encountered.
        /// Indicates the sector has a monkey swing ceiling.
        /// Boxes with monkey swing get the MONKEY_BOX_FLAG.
        /// </summary>
        private bool dec_monkey;
        private int dec_monkeyCeiling;

        /// <summary>
        /// Current flip state being processed.
        /// False = processing normal rooms, True = processing flipped rooms.
        /// </summary>
        private bool dec_flipped;

        /// <summary>
        /// Flag set by Dec_CheckOverlap when boxes are connected via a jump.
        /// Used to set the JUMP_BIT (0x0800) flag on the overlap.
        /// </summary>
        private bool dec_jump;


        /// <summary>
        /// Current room being processed.
        /// Updated by Dec_ClampRoom and Dec_GetHeight as they traverse portals.
        /// </summary>
        private Room dec_room;

        /// <summary>
        /// Floor corner heights for the current sector.
        /// Used to detect slopes and calculate average floor height.
        /// Naming: dec_cornerHeight[1-4] corresponds to XnZp, XpZp, XpZn, XnZn corners.
        /// </summary>
        private int dec_cornerHeight1 = Clicks.ToWorld(-1);  // Floor.XnZp (X-, Z+)
        private int dec_cornerHeight2 = Clicks.ToWorld(-1);  // Floor.XpZp (X+, Z+)
        private int dec_cornerHeight3 = Clicks.ToWorld(-1);  // Floor.XpZn (X+, Z-)
        private int dec_cornerHeight4 = Clicks.ToWorld(-1);  // Floor.XnZn (X-, Z-)

        /// <summary>
        /// List of generated boxes (internal format).
        /// Converted to _boxes (TombEngineBox format) after generation.
        /// </summary>
        private List<dec_TombEngine_box_aux> dec_boxes;

        /// <summary>
        /// List of generated overlaps.
        /// Copied directly to _overlaps after generation.
        /// </summary>
        private List<TombEngineOverlap> dec_overlaps;

        /// <summary>
        /// Flag set when traversing through a door/portal.
        /// Used to handle boxes that span multiple rooms.
        /// </summary>
        private bool dec_doorCheck;

        /// <summary>
        /// Sentinel value indicating an invalid/unwalkable height.
        /// Returns from Dec_GetHeight when a sector cannot be walked on.
        /// </summary>
        private const int _noHeight = int.MinValue + byte.MaxValue;
        private const int _noMonkeyCeiling = int.MinValue;
        private const int _monkeySwingCeilingLimit = 320;


        /// <summary>
        /// Main entry point for box and overlap generation.
        ///
        /// ALGORITHM:
        /// ==========
        /// Pass 1 (flipped=0): Process all base/normal rooms
        /// Pass 2 (flipped=1): Process all alternate/flipped rooms
        ///
        /// For each room, iterate through all non-border sectors and:
        /// 1. Try to create a box starting from that sector (Dec_GetBox)
        /// 2. Add the box to the list if valid, deduplicating (Dec_AddBox)
        /// 3. Store the box index in the sector for runtime lookup
        ///
        /// After all boxes are created, build the overlap connections (Dec_BuildOverlaps).
        ///
        /// NOTE: The original code called FlipAllRooms() to swap room pointers.
        /// Here we use the dec_flipped flag to track state instead.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void Dec_BuildBoxesAndOverlaps()
        {
            dec_room = _level.Rooms[0];
            dec_boxes = new List<dec_TombEngine_box_aux>();

            Stopwatch watch = new Stopwatch();
            watch.Start();

            // ===================================================================================
            // BOX GENERATION - Two passes for flip states
            // ===================================================================================
            for (int flipped = 0; flipped < 2; flipped++)
            {
                for (int i = 0; i < _level.Rooms.Length; i++)
                {
                    Room room = _level.Rooms[i];

                    // Filter rooms based on flip state:
                    // - Pass 0 (flipped=0): Process rooms WITHOUT an alternate base (base rooms + non-flip rooms)
                    // - Pass 1 (flipped=1): Process rooms WITH an alternate base (flipped versions)
                    if (room != null && (flipped == 0 && room.AlternateBaseRoom == null || flipped == 1 && room.AlternateBaseRoom != null))
                    {
                        TombEngineRoom tempRoom = _tempRooms[room];

                        // Iterate through all sectors in the room
                        for (int z = 0; z < room.NumZSectors; z++)
                        {
                            for (int x = 0; x < room.NumXSectors; x++)
                            {
                                int boxIndex = -1;  // -1 = no box for this sector

                                // Skip rooms excluded from pathfinding
                                if (!room.Properties.FlagExcludeFromPathFinding)
                                {
                                    dec_TombEngine_box_aux box = new dec_TombEngine_box_aux();

                                    // Skip border sectors (x=0, z=0, x=max, z=max)
                                    // Border sectors are always walls in TR format
                                    if (x != 0 &&
                                        z != 0 &&
                                        x != room.NumXSectors - 1 &&
                                        z != room.NumZSectors - 1 &&
                                        Dec_GetBox(box, x, z, room))  // Try to create/expand a box
                                    {
                                        // Add box to list (may return existing index if duplicate)
                                        boxIndex = Dec_AddBox(box);
                                        if (boxIndex < 0) return;  // Error condition
                                    }
                                }

                                // Store box index in sector for runtime lookup
                                // Sector array is stored as [x * NumZSectors + z]
                                tempRoom.Sectors[tempRoom.NumZSectors * x + z].BoxIndex = boxIndex;
                            }
                        }

                        _tempRooms[room] = tempRoom;
                    }
                }

                // Switch to flipped room processing
                // Original code called FlipAllRooms() which swapped room pointers.
                // We use a flag instead since TombEditor's data model doesn't support room swapping.
                dec_flipped = true;
            }

            dec_flipped = false;

            watch.Stop();
            Console.WriteLine("Dec_BuildBoxesAndOverlaps() -> Build boxes: " + watch.ElapsedMilliseconds + " ms, Count = " + dec_boxes.Count);

            watch.Restart();

            // ===================================================================================
            // OVERLAP GENERATION
            // ===================================================================================
            Dec_BuildOverlaps();

            watch.Stop();
            Console.WriteLine("Dec_BuildBoxesAndOverlaps() -> Build overlaps: " + watch.ElapsedMilliseconds + " ms, Count = " + dec_overlaps.Count);
        }

        /// <summary>
        /// Builds overlap connections between all box pairs.
        ///
        /// OVERLAP DATA FORMAT:
        /// ====================
        /// Overlaps are stored as a flat array. Each box has an OverlapIndex pointing
        /// to the start of its overlap list. The list ends when END_BIT (0x8000) is set.
        ///
        /// OVERLAP FLAGS:
        /// - 0x0800 (JUMP_BIT): Connection requires jumping across a gap
        /// - 0x2000 (MONKEY_BIT): Connection uses monkey swing ceiling
        /// - 0x8000 (END_BIT): Last overlap in this box's list
        ///
        /// FLIP STATE HANDLING:
        /// ====================
        /// Boxes in non-flipped rooms get both Unflipped and Flipped flags set,
        /// allowing them to connect to boxes in either state.
        ///
        /// Boxes in flip pairs only have one flag set, ensuring they only connect
        /// to boxes in the same flip state.
        ///
        /// The algorithm processes overlaps in two passes:
        /// 1. Unflipped pass: Check box pairs where both have Unflipped flag
        /// 2. Flipped pass: Check box pairs where both have Flipped flag (skip if already done)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private bool Dec_BuildOverlaps()
        {
            dec_overlaps = new List<TombEngineOverlap>();

            if (dec_boxes.Count == 0)
                return false;

            int i = 0;
            int j = 0;

            // ===================================================================================
            // PRE-PROCESS: Set flip flags for boxes in non-flipped rooms
            // ===================================================================================
            // Boxes in rooms without flip pairs should be accessible from both flip states.
            // Primary path: Dec_GetBox now stamps both Unflipped and Flipped on boxes whose
            // owning room is not Alternated, so this loop is normally a no-op. It is kept
            // as a safety net for any code path that might add a box without going through
            // the room.Alternated check (e.g. third-party patches that bypass Dec_GetBox).
            for (int k = 0; k < dec_boxes.Count; k++)
            {
                if (!_tempRooms[dec_boxes[k].Room].Flipped)
                {
                    dec_boxes[k].Unflipped = true;
                    dec_boxes[k].Flipped = true;
                }
            }

            // ===================================================================================
            // PRE-PROCESS: Merge flip-state flags between flip-pair partner boxes
            // ===================================================================================
            // When a Pass 0 (base) box and a Pass 1 (alt) box geometrically overlap (2D bbox
            // intersection) AND share the same floor height, they represent the same physical
            // floor area in different flip states -- one is the base version, the other the
            // alternate. In every flip state at runtime the underlying sectors point to one or
            // the other of these boxes, so they must logically belong to the same flip-state
            // zone cluster.
            //
            // Without this merge, a base-only box (Unflipped=true, Flipped=false) that shares
            // area with an alt-only box becomes zone-isolated in the flipped pass. Creatures
            // that pass between the base box (via a non-alternated neighbour room) and the alt
            // box (in the active flipped room) trigger ZONE_MISMATCH and get pushed back. This
            // is the "yeti vault" symptom: vault triggers, creature climbs up onto a sector
            // whose box is the base-only floor box, runtime sees its zone differs from the
            // creature's stored box in cluster 1, pushes it back.
            //
            // O(N^2) but N is hundreds at most -- negligible relative to overall compile time.
            for (int a = 0; a < dec_boxes.Count; a++)
            {
                var boxA = dec_boxes[a];
                // Skip boxes that already carry both flags -- nothing to learn from a partner.
                if (boxA.Unflipped && boxA.Flipped)
                    continue;

                for (int b = 0; b < dec_boxes.Count; b++)
                {
                    if (a == b)
                        continue;

                    var boxB = dec_boxes[b];

                    // Must be same floor height -- different heights mean different physical
                    // floors (e.g. a stair box at one click up does NOT pair with the surface
                    // it sits on).
                    if (boxA.Height != boxB.Height)
                        continue;

                    // STRICT: bounds must be IDENTICAL, not just overlapping. Two boxes
                    // representing the same physical area in different flip states will
                    // have the same bbox -- if one box's bounds are a strict subset of the
                    // other's, they cover DIFFERENT physical extents and are NOT flip
                    // partners. Example bug they previously caused: in alt geometry a 5x1
                    // sector strip becomes a staircase (1-sector boxes at varying heights);
                    // the base 5-sector box (Unflipped only) would falsely get Flipped=true
                    // from a 1-sector alt box at one end that happened to share a sector
                    // and matched its height. BFS in alt then routed creatures THROUGH the
                    // base box's physical extent, into the alt-only blocks.
                    if (boxA.Xmin != boxB.Xmin || boxA.Xmax != boxB.Xmax)
                        continue;
                    if (boxA.Zmin != boxB.Zmin || boxA.Zmax != boxB.Zmax)
                        continue;

                    // True flip-pair partners (identical bounds + height). Merge flags.
                    dec_boxes[a].Unflipped |= boxB.Unflipped;
                    dec_boxes[a].Flipped   |= boxB.Flipped;

                    // Early-out if A is now fully flagged.
                    if (dec_boxes[a].Unflipped && dec_boxes[a].Flipped)
                        break;
                }
            }

            // ===================================================================================
            // MAIN LOOP: Check all box pairs for overlaps
            // ===================================================================================
            do
            {
                var box1 = dec_boxes[i];
                dec_boxes[i].OverlapIndex = -1;  // Reset overlap index

                int numOverlapsAdded = 0;

                // Track Pass 1 entries by target box so Pass 2 can merge into the
                // existing entry (set FlippedValid) instead of adding a duplicate.
                // Key = j (target box index), Value = index into dec_overlaps.
                var pass1Index = new Dictionary<int, int>();

                // ===============================================================================
                // PASS 1: Check overlaps in UNFLIPPED state
                // ===============================================================================
                if (box1.Unflipped)
                {
                    if (dec_flipped)
                    {
                        dec_flipped = false;
                    }

                    j = 0;
                    do
                    {
                        if (i != j)  // Don't check box against itself
                        {
                            var box2 = dec_boxes[j];

                            // Only check if box2 also exists in unflipped state
                            if (box2.Unflipped)
                            {
                                if (Dec_CheckOverlap(box1, box2))
                                {
                                    // First overlap for this box - record starting index
                                    if (dec_boxes[i].OverlapIndex == -1)
                                        dec_boxes[i].OverlapIndex = dec_overlaps.Count;

                                    // Create overlap entry tagged as valid in UNFLIPPED state.
                                    var overlap = new TombEngineOverlap
                                    {
                                        Box = j,
                                        Flags = OverlapFlags.UnflippedValid
                                    };

                                    // Set capability flags based on Dec_CheckOverlap results
                                    if (dec_jump)
                                        overlap.Flags |= OverlapFlags.Jump;   // JUMP_BIT
                                    if (dec_monkey)
                                        overlap.Flags |= OverlapFlags.Monkey;  // MONKEY_BIT

                                    // Set AmphibiousTraversable flag.
                                    // Wet<->Wet (deep OR shallow water on either side): always
                                    // traversable -- an amphibious creature swims/wades over the
                                    // underwater floor step (it travels at the surface, so a deep
                                    // floor next to shallow water is no obstacle). Previously a
                                    // deep-water <-> shallow-water edge with a big floor step was
                                    // NOT marked traversable, so a crocodile could get trapped in
                                    // a deep pool and never swim to a shallow exit ramp.
                                    // Dry<->Dry: floor step <= 1 click (normal land walking).
                                    // Wet<->Dry (the WATERLINE climb-out): STRICTLY below 1 click.
                                    // A flat shallow shelf sitting a full click under the shore
                                    // is not physically climbable from the water -- the creature
                                    // needs an INCLINED shallow sector whose floor rises to meet
                                    // the land (its box height averages the slope, ~half a click,
                                    // and passes the strict test). With <=, the flat ledge edge
                                    // got flagged, zones merged across it, and the creature kept
                                    // targeting enemies behind an exit it could never use.
                                    bool box1Wet = box1.Water || box1.Shallow;
                                    bool box2Wet = box2.Water || box2.Shallow;
                                    int heightDiff = Math.Abs(box1.Height - box2.Height);
                                    bool crossesWaterline = box1Wet != box2Wet;

                                    if ((box1Wet && box2Wet) ||
                                        (crossesWaterline ? heightDiff < Clicks.ToWorld(1) : heightDiff <= Clicks.ToWorld(1)))
                                        overlap.Flags |= OverlapFlags.AmphibiousTraversable;

                                    pass1Index[j] = dec_overlaps.Count;
                                    dec_overlaps.Add(overlap);
                                    numOverlapsAdded++;
                                }
                            }
                        }

                        j++;
                    }
                    while (j < dec_boxes.Count);
                }

                // ===============================================================================
                // PASS 2: Check overlaps in FLIPPED state
                // ===============================================================================
                // Previously skipped when both boxes carried Unflipped flag, on the assumption
                // Pass 1 had already established the same overlap. That assumption is FALSE for
                // boxes whose adjacency check straddles a portal into an alternated room:
                // Pass 1 (dec_flipped=false) traverses base geometry; the same pair re-checked
                // in Pass 2 (dec_flipped=true) traverses alt geometry. A new alt wall would
                // make Pass 2 reject what Pass 1 accepted -- but the skip meant Pass 2 never
                // ran, so the stale base overlap remained in the chain and BFS routed through
                // alt walls.
                //
                // Now we always run Pass 2 for box1.Flipped. If the same (i,j) pair was
                // already added in Pass 1, we OR in FlippedValid on the existing entry
                // instead of duplicating. If Pass 1 didn't add it (or said no overlap), we
                // emit a new entry tagged FlippedValid only.
                if (box1.Flipped)
                {
                    if (!dec_flipped)
                    {
                        dec_flipped = true;
                    }

                    j = 0;
                    do
                    {
                        if (i != j)
                        {
                            var box2 = dec_boxes[j];

                            // Only check if box2 also exists in flipped state
                            if (box2.Flipped)
                            {
                                if (Dec_CheckOverlap(box1, box2))
                                {
                                    if (pass1Index.TryGetValue(j, out int existingIdx))
                                    {
                                        // Pair already added by Pass 1; merge flip flag.
                                        var existing = dec_overlaps[existingIdx];
                                        existing.Flags |= OverlapFlags.FlippedValid;
                                        dec_overlaps[existingIdx] = existing;
                                    }
                                    else
                                    {
                                        if (dec_boxes[i].OverlapIndex == -1)
                                            dec_boxes[i].OverlapIndex = dec_overlaps.Count;

                                        var overlap = new TombEngineOverlap
                                        {
                                            Box = j,
                                            Flags = OverlapFlags.FlippedValid
                                        };

                                        if (dec_jump)
                                            overlap.Flags |= OverlapFlags.Jump;
                                        if (dec_monkey)
                                            overlap.Flags |= OverlapFlags.Monkey;

                                        // Set AmphibiousTraversable flag (see Pass 1 comment).
                                        // Wet<->Wet always traversable; Dry<->Dry step <= 1 click;
                                        // Wet<->Dry (waterline climb-out) STRICTLY below 1 click --
                                        // only an inclined shallow exit sector qualifies.
                                        bool box1Wet = box1.Water || box1.Shallow;
                                        bool box2Wet = box2.Water || box2.Shallow;
                                        int heightDiff = Math.Abs(box1.Height - box2.Height);
                                        bool crossesWaterline = box1Wet != box2Wet;

                                        if ((box1Wet && box2Wet) ||
                                            (crossesWaterline ? heightDiff < Clicks.ToWorld(1) : heightDiff <= Clicks.ToWorld(1)))
                                            overlap.Flags |= OverlapFlags.AmphibiousTraversable;

                                        dec_overlaps.Add(overlap);
                                        numOverlapsAdded++;
                                    }
                                }
                            }
                        }

                        j++;
                    }
                    while (j < dec_boxes.Count);
                }

                i++;

                // Mark end of this box's overlap list with END_BIT
                if (numOverlapsAdded != 0)
                    dec_overlaps[dec_overlaps.Count - 1].Flags |= OverlapFlags.End;  // END_BIT
            }
            while (i < dec_boxes.Count);

            dec_flipped = false;

            return true;
        }

        /// <summary>
        /// Adds a box to the box list, with deduplication.
        ///
        /// DEDUPLICATION:
        /// ==============
        /// Boxes are considered duplicates if they have identical:
        /// - Bounds (Xmin, Xmax, Zmin, Zmax)
        /// - Height
        /// - Water flag
        ///
        /// This is critical because the same floor area may be processed multiple times
        /// (from different starting sectors, or during flip state passes).
        ///
        /// SIMD OPTIMIZATION:
        /// ==================
        /// Uses SSE2 SIMD instructions when available to compare multiple values
        /// simultaneously, significantly speeding up the search for duplicates.
        ///
        /// FLIP STATE HANDLING:
        /// ====================
        /// When a duplicate is found during the flipped pass (dec_flipped=true),
        /// the existing box is marked as Flipped, indicating it exists in both states.
        /// </summary>
        /// <param name="box">Box to add</param>
        /// <returns>Index of the box (new or existing duplicate)</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private int Dec_AddBox(dec_TombEngine_box_aux box)
        {
            int boxIndex = -1;

            // ===================================================================================
            // SIMD-ACCELERATED DUPLICATE SEARCH
            // ===================================================================================
            if (Sse2.IsSupported)
            {
                // Pack search criteria into 128-bit vectors (4 x 32-bit integers)
                var searchBounds = Vector128.Create(box.Xmin, box.Xmax, box.Zmin, box.Zmax);

                for (int i = 0; i < dec_boxes.Count; i++)
                {
                    var candidate = dec_boxes[i];

                    // Pack candidate values
                    var candBounds = Vector128.Create(candidate.Xmin, candidate.Xmax, candidate.Zmin, candidate.Zmax);

                    // Compare all 4 bounds simultaneously and extract comparison results as bitmasks
                    var cmpBounds = Sse2.CompareEqual(searchBounds, candBounds);
                    int maskBounds = Sse2.MoveMask(cmpBounds.AsByte());

                    // All 4 bounds must match (0xFFFF = all 16 bytes equal)
                    if (maskBounds == 0xFFFF &&
                        candidate.Height == box.Height &&
                        candidate.Monkey == box.Monkey &&
                        (!box.Monkey || candidate.MonkeyCeiling == box.MonkeyCeiling))
                    {
                        boxIndex = i;
                        break;
                    }
                }
            }
            else
            {
                // ===================================================================================
                // SCALAR FALLBACK (non-SSE2 systems)
                // ===================================================================================
                for (int i = 0; i < dec_boxes.Count; i++)
                {
                    if (dec_boxes[i].Xmin == box.Xmin &&
                        dec_boxes[i].Xmax == box.Xmax &&
                        dec_boxes[i].Zmin == box.Zmin &&
                        dec_boxes[i].Zmax == box.Zmax &&
                        dec_boxes[i].Height == box.Height &&
                        dec_boxes[i].Monkey == box.Monkey &&
                        (!box.Monkey || dec_boxes[i].MonkeyCeiling == box.MonkeyCeiling))
                    {
                        boxIndex = i;
                        break;
                    }
                }
            }

            // ===================================================================================
            // ADD OR UPDATE BOX
            // ===================================================================================
            if (boxIndex == -1)
            {
                // No duplicate found - add as new box
                boxIndex = dec_boxes.Count;
                box.OverlapIndex = -1;
                dec_boxes.Add(box);
            }
            else
            {
                // Update room reference, if water/shallow
                if (box.Water || box.Shallow)
                    dec_boxes[boxIndex].Room = box.Room;

                // Duplicate found - merge flags. Both Unflipped and Flipped are OR'd so
                // that the order in which duplicates are encountered (e.g. base then
                // alternate, or non-alternated room then alternated room) cannot lose
                // a flag. Previously only Flipped was merged, which silently dropped
                // Unflipped if a box from a non-alternated room was added AFTER a
                // geometrically identical box from an alternate-side pass.
                dec_boxes[boxIndex].Unflipped |= box.Unflipped;
                dec_boxes[boxIndex].Flipped   |= box.Flipped;
                dec_boxes[boxIndex].Water     |= box.Water;
                dec_boxes[boxIndex].Shallow   |= box.Shallow;
                dec_boxes[boxIndex].Monkey    |= box.Monkey;
            }

            return boxIndex;
        }

        /// <summary>
        /// Creates a box starting from a specific sector, expanding in all 4 directions.
        ///
        /// BOX EXPANSION ALGORITHM (Spiral Expansion):
        /// ============================================
        /// Starting from the initial sector, the algorithm expands outward in all 4 directions
        /// simultaneously until it can no longer expand in any direction.
        ///
        /// Directions are encoded as bits in a direction mask:
        /// - 0x01: Expand Xmin (left, -X direction)
        /// - 0x02: Expand Xmax (right, +X direction)
        /// - 0x04: Expand Zmin (down, -Z direction, "top" in original)
        /// - 0x08: Expand Zmax (up, +Z direction, "bottom" in original)
        ///
        /// Expansion in a direction stops when:
        /// - Floor height changes
        /// - Monkey swing state changes
        /// - Encounter a wall or unwalkable sector
        /// - Cross into a room with different flip state
        ///
        /// SPLITTER BOXES:
        /// ===============
        /// If a SPLITTER is encountered, the box becomes a 1x1 splitter box.
        /// Splitter boxes are used to create fine-grained AI control points.
        ///
        /// GHOST BLOCKS:
        /// =============
        /// Ghost blocks override actual floor geometry for pathfinding purposes.
        /// Used to create invisible walkways or barriers for AI.
        /// </summary>
        /// <param name="box">Box structure to fill</param>
        /// <param name="x">Starting sector X (local to room)</param>
        /// <param name="z">Starting sector Z (local to room)</param>
        /// <param name="theRoom">Room containing the starting sector</param>
        /// <returns>True if a valid box was created, false otherwise</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private bool Dec_GetBox(dec_TombEngine_box_aux box, int x, int z, Room theRoom)
        {
            bool monkeyInit = false;

            Room room = theRoom;
            Sector sector = room.Sectors[x, z];

            // ===================================================================================
            // INITIAL SECTOR VALIDATION
            // ===================================================================================
            // Check if current sector is not walkable (sector flag)
            if ((sector.Flags & SectorFlags.NotWalkableFloor) != 0) return false;

            // Check for walls and solid portals
            if (sector.Type == SectorType.Wall ||
                sector.Type == SectorType.BorderWall ||
                sector.WallPortal != null && sector.WallPortal.Opacity == PortalOpacity.SolidFaces)
            {
                return false;
            }

            // ===================================================================================
            // GET INITIAL FLOOR HEIGHTS
            // ===================================================================================
            // Use ghost block if present, otherwise use actual floor
            // Ghost blocks override floor geometry for AI purposes
            dec_cornerHeight1 = sector.HasGhostBlock ? sector.GhostBlock.Floor.XnZp : sector.Floor.XnZp;
            dec_cornerHeight2 = sector.HasGhostBlock ? sector.GhostBlock.Floor.XpZp : sector.Floor.XpZp;
            dec_cornerHeight3 = sector.HasGhostBlock ? sector.GhostBlock.Floor.XpZn : sector.Floor.XpZn;
            dec_cornerHeight4 = sector.HasGhostBlock ? sector.GhostBlock.Floor.XnZn : sector.Floor.XnZn;

            // Convert local coordinates to world sector coordinates
            int currentX = room.Position.X + x;
            int currentZ = room.Position.Z + z;

            dec_room = theRoom;

            // Traverse through portals to find the actual room at this position
            Dec_ClampRoom(currentX, currentZ);

            // Reset state flags for Dec_GetHeight
            dec_splitter = false;
            dec_checkUnderwater = true;
            dec_shallowWater = false;
            dec_monkey = false;
            dec_monkeyCeiling = _noMonkeyCeiling;
            
            // ===================================================================================
            // GET INITIAL FLOOR HEIGHT AND PROPERTIES
            // ===================================================================================
            bool slope;
            int floor = Dec_GetHeight(currentX, currentZ, out slope);
            box.Height = floor;
            box.Slope = slope;

            // Sector is not walkable
            if (floor == _noHeight) return false;

            // Set box room and water state
            box.Room = dec_room;
            box.Water = dec_room.Properties.Type == RoomType.Water;

            // Set flip state flags.
            //
            // If the room is NOT part of a flip pair, the box logically exists in both
            // flip states (the room's geometry never changes). Mark BOTH flags so the box
            // is picked up as an overlap candidate in both Pass 1 (Unflipped) and Pass 2
            // (Flipped) of Dec_BuildOverlaps, and likewise considered by zone generation in
            // both passes. This is critical for cross-portal connectivity between a
            // non-alternated room (e.g. a lower stack room) and the alternate of an
            // adjacent alternated room (e.g. the flipped upper stack room with a
            // repositioned ladder/stairs). Without this, a box in the non-alternated
            // room would only carry Unflipped=true and Pass 2 would never pair it with
            // boxes from the alternate room, producing the "creature bumps into the
            // vertical portal after flipmap" symptom.
            if (!dec_room.Alternated)
            {
                box.Unflipped = true;
                box.Flipped = true;
            }
            else if (dec_flipped)
            {
                box.Flipped = true;
            }
            else
            {
                box.Unflipped = true;
            }

            // Record initial monkey swing state
            if (dec_monkey)
            {
                box.Monkey = true;
                box.MonkeyCeiling = dec_monkeyCeiling;
                monkeyInit = true;
            }
            else
            {
                box.MonkeyCeiling = _noMonkeyCeiling;
            }

            // Handle shallow water (water depth <= 1 click)
            // Shallow water is treated as dry land for pathfinding
            if (!dec_checkUnderwater)
            {
                box.Water = false;
            }
            box.Shallow = dec_shallowWater;

            // ===================================================================================
            // SPLITTER BOX - Single sector box
            // ===================================================================================
            if (dec_splitter)
            {
                // SPLITTER encountered - create 1x1 box
                box.Xmin = currentX;
                box.Zmin = currentZ;
                box.Xmax = currentX + 1;
                box.Zmax = currentZ + 1;
                box.Splitter = true;

                return true;
            }
            else if (monkeyInit)
            {
                box.Xmin = currentX;
                box.Zmin = currentZ;
                box.Xmax = currentX + 1;
                box.Zmax = currentZ + 1;

                return true;
            }
            else
            {
                // ===================================================================================
                // SPIRAL EXPANSION - Expand box in all 4 directions
                // ===================================================================================
                // Set splitter flag to prevent infinite recursion
                // (Dec_GetHeight will now return _noHeight if it hits another TT_SPLITTER)
                dec_splitter = true;

                // Direction bitmask: 0x01=left, 0x02=right, 0x04=down(-Z), 0x08=up(+Z)
                int direction = 0x0f;      // All directions active
                int directionBase = 0x0f;

                // Current box bounds (will be expanded)
                int xMin = currentX;
                int xMax = currentX;
                int zMin = currentZ;
                int zMax = currentZ;

                // Track current room for each expansion direction
                // This allows the box to span multiple connected rooms
                Room currentRoom = theRoom;
                Room currentRoom1 = theRoom;  // Direction 0x04 (Zmin, -Z)
                Room currentRoom2 = theRoom;  // Direction 0x02 (Xmax, +X)
                Room currentRoom3 = theRoom;  // Direction 0x08 (Zmax, +Z)
                Room currentRoom4 = theRoom;  // Direction 0x01 (Xmin, -X)

                int searchX = xMin;
                int searchZ = zMin;

                // Main expansion loop - continues until no direction can expand
                while (true)
                {
                    // ===============================================================================
                    // DIRECTION 0x04: Expand Zmin (-Z direction, "top" in original)
                    // ===============================================================================
                    if ((directionBase & 0x04) == 0x04)
                    {
                        dec_doorCheck = false;
                        dec_room = currentRoom1;
                        currentRoom = currentRoom1;

                        searchX = xMin;

                        if (xMin <= xMax)
                        {
                            bool finishedDirection = true;

                            // Check all sectors along the current edge
                            while (floor == Dec_GetHeight(searchX, zMin) &&
                                   floor == Dec_GetHeight(searchX, zMin - 1) &&  // Check the row we want to expand into
                                   dec_monkey == monkeyInit)
                            {
                                // Update room reference at start of edge
                                if (searchX == xMin) currentRoom1 = dec_room;

                                // Handle room transitions
                                if (dec_doorCheck)
                                {
                                    // Stop if crossing into room with different flip state
                                    if (dec_room != currentRoom &&
                                        (dec_room.Alternated ||
                                         currentRoom.Alternated))
                                    {
                                        break;
                                    }

                                    // Reset to original room and verify floor height
                                    dec_room = currentRoom;

                                    if (floor != Dec_GetHeight(searchX, zMin - 1)) break;

                                    dec_doorCheck = false;
                                }

                                searchX++;

                                // Successfully checked entire edge
                                if (searchX > xMax)
                                {
                                    finishedDirection = false;
                                    break;
                                }
                            }

                            // Direction finished if we couldn't check entire edge
                            if (finishedDirection) direction -= 0x04;
                        }

                        directionBase = direction;
                        // Expand bounds if direction still active
                        if ((directionBase & 0x04) == 0x04) zMin--;
                    }

                    // ===============================================================================
                    // DIRECTION 0x02: Expand Xmax (+X direction, "right" in original)
                    // ===============================================================================
                    if ((directionBase & 0x02) == 0x02)
                    {
                        dec_doorCheck = false;
                        dec_room = currentRoom2;
                        currentRoom = currentRoom2;

                        searchZ = zMin;

                        if (zMin <= zMax)
                        {
                            bool finishedDirection = true;

                            while (floor == Dec_GetHeight(xMax, searchZ) &&
                                   floor == Dec_GetHeight(xMax + 1, searchZ) &&
                                   dec_monkey == monkeyInit)
                            {
                                if (searchZ == zMin) currentRoom2 = dec_room;

                                if (dec_doorCheck)
                                {
                                    if (dec_room != currentRoom &&
                                        (dec_room.Alternated ||
                                         currentRoom.Alternated))
                                    {
                                        break;
                                    }

                                    dec_room = currentRoom;

                                    if (floor != Dec_GetHeight(xMax + 1, searchZ)) break;

                                    dec_doorCheck = false;
                                }

                                searchZ++;

                                if (searchZ > zMax)
                                {
                                    finishedDirection = false;
                                    break;
                                }
                            }

                            if (finishedDirection) direction -= 0x02;
                        }

                        directionBase = direction;
                        if ((directionBase & 0x02) == 0x02) xMax++;
                    }

                    // ===============================================================================
                    // DIRECTION 0x08: Expand Zmax (+Z direction, "bottom" in original)
                    // ===============================================================================
                    if ((directionBase & 0x08) == 0x08)
                    {
                        dec_doorCheck = false;
                        dec_room = currentRoom3;
                        currentRoom = currentRoom3;

                        searchX = xMax;

                        if (xMax >= xMin)
                        {
                            bool finishedDirection = true;

                            while (floor == Dec_GetHeight(searchX, zMax) &&
                                   floor == Dec_GetHeight(searchX, zMax + 1) &&
                                   dec_monkey == monkeyInit)
                            {
                                if (searchX == xMax) currentRoom3 = dec_room;

                                if (dec_doorCheck)
                                {
                                    if (dec_room != currentRoom &&
                                        (dec_room.Alternated ||
                                         currentRoom.Alternated))
                                    {
                                        break;
                                    }

                                    dec_room = currentRoom;

                                    if (floor != Dec_GetHeight(searchX, zMax + 1)) break;

                                    dec_doorCheck = false;
                                }

                                searchX--;

                                if (searchX < xMin)
                                {
                                    finishedDirection = false;
                                    break;
                                }
                            }

                            if (finishedDirection) direction -= 0x08;
                        }

                        directionBase = direction;
                        if ((directionBase & 0x08) == 0x08) zMax++;
                    }

                    // ===============================================================================
                    // DIRECTION 0x01: Expand Xmin (-X direction, "left" in original)
                    // ===============================================================================
                    if ((directionBase & 0x01) == 0x01)
                    {
                        dec_doorCheck = false;
                        dec_room = currentRoom4;
                        currentRoom = currentRoom4;

                        searchZ = zMax;

                        if (zMax >= zMin)
                        {
                            bool finishedDirection = true;

                            while (floor == Dec_GetHeight(xMin, searchZ) &&
                                   floor == Dec_GetHeight(xMin - 1, searchZ) &&
                                   dec_monkey == monkeyInit)
                            {
                                if (searchZ == zMax) currentRoom4 = dec_room;

                                if (dec_doorCheck)
                                {
                                    if (dec_room != currentRoom &&
                                        (dec_room.Alternated ||
                                         currentRoom.Alternated))
                                    {
                                        break;
                                    }

                                    dec_room = currentRoom;

                                    if (floor != Dec_GetHeight(xMin - 1, searchZ)) break;

                                    dec_doorCheck = false;
                                }

                                searchZ--;

                                if (searchZ < zMin)
                                {
                                    finishedDirection = false;
                                    break;
                                }
                            }

                            if (finishedDirection) direction -= 0x01;
                        }

                        directionBase = direction;
                        if ((directionBase & 0x01) == 0x01) xMin--;
                    }

                    // All directions exhausted - expansion complete
                    if (directionBase == 0x00) break;

                    currentX = xMin;
                }

                // ===================================================================================
                // SET FINAL BOX BOUNDS
                // ===================================================================================
                // Note: Xmax and Zmax are exclusive (one past the last included sector)
                box.Xmin = xMin;
                box.Zmin = zMin;
                box.Xmax = xMax + 1;
                box.Zmax = zMax + 1;

                return true;
            }
        }

        /// <summary>
        /// Traverses through portals to find the room containing a world position.
        ///
        /// This function handles two types of portal traversal:
        /// 1. WALL PORTALS: Horizontal connections between rooms
        /// 2. FLOOR PORTALS: Vertical connections (stacked rooms)
        ///
        /// WALL PORTAL TRAVERSAL:
        /// =====================
        /// If the target position is outside the current room bounds, the function
        /// clamps to the nearest border sector and follows any wall portal there.
        /// This continues until the position is within a room's bounds.
        ///
        /// FLOOR PORTAL TRAVERSAL:
        /// =======================
        /// After horizontal positioning, the function descends through floor portals
        /// until it reaches the actual floor (stops at water boundaries).
        ///
        /// CORNER HANDLING:
        /// ================
        /// Corner sectors (0,0), (0,max), (max,0), (max,max) are problematic because
        /// they have no portal direction. The function returns false if stuck in a corner.
        /// </summary>
        /// <param name="x">World X coordinate (in sectors)</param>
        /// <param name="z">World Z coordinate (in sectors)</param>
        /// <returns>True if position is reachable, false if blocked</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private bool Dec_ClampRoom(int x, int z)
        {
            Room room = dec_room;

            int localX = 0;
            int localZ = 0;

            Sector sector;

            // ===================================================================================
            // PHASE 1: WALL PORTAL TRAVERSAL
            // ===================================================================================
            // Loop while position is outside current room bounds
            while (Dec_NeedsClamping(x, z))
            {
                // Convert world coords to local room coords
                localX = x - room.Position.X;
                localZ = z - room.Position.Z;

                // Clamp to room bounds (find border sector to check for portal)
                if (localX >= 0)
                {
                    if (localX >= room.NumXSectors)
                        localX = room.NumXSectors - 1;
                }
                else
                {
                    localX = 0;
                }

                if (localZ >= 0)
                {
                    if (localZ >= room.NumZSectors)
                        localZ = room.NumZSectors - 1;
                }
                else
                {
                    localZ = 0;
                }

                sector = room.Sectors[localX, localZ];

                // CORNER DETECTION HACK:
                // Original code didn't handle corners. If we land in a corner sector,
                // there's no portal to follow (portals are on edges, not corners).
                // This can happen with 3+ rooms connected at a point.
                if (localX == 0 && localZ == 0 ||
                    localX == 0 && localZ == room.NumZSectors - 1 ||
                    localX == room.NumXSectors - 1 && localZ == 0 ||
                    localX == room.NumXSectors - 1 && localZ == room.NumZSectors - 1)
                    return false;

                // No wall portal - can't reach the position
                if (sector.WallPortal == null)
                    break;

                // Follow wall portal to adjoining room. When processing the flipped pass
                // and the adjoining room is itself alternated, descend into its alternate
                // so geometry sampled across the portal matches the flip state we're
                // currently building. Without this, the alt pass would silently read
                // base-room sectors across wall portals, producing wrong heights and
                // "connecting box" splits at the junction between flip-changed and
                // unchanged geometry.
                Room adjoiningRoom = sector.WallPortal.AdjoiningRoom;
                if (adjoiningRoom.AlternateRoom != null && dec_flipped)
                    adjoiningRoom = adjoiningRoom.AlternateRoom;

                dec_room = adjoiningRoom;
                room = adjoiningRoom;

                // Stop if portal is solid (toggle opacity 1)
                if (sector.WallPortal.Opacity == PortalOpacity.SolidFaces)
                    return false;
            }

            room = dec_room;

            localX = x - room.Position.X;
            localZ = z - room.Position.Z;

            // Safety check for ultra-rare edge cases
            if (localX < 0 || localZ < 0 || localX >= room.NumXSectors || localZ >= room.NumZSectors)
                return false;

            sector = room.Sectors[localX, localZ];

            // ===================================================================================
            // PHASE 2: FLOOR PORTAL TRAVERSAL
            // ===================================================================================
            // Descend through floor portals to find the actual floor room.
            // Stops at water boundaries (water/land transition).
            while (room.GetFloorRoomConnectionInfo(new VectorInt2(localX, localZ), true).TraversableType == Room.RoomConnectionType.FullPortal)
            {
                // Resolve adjoining room with respect to the current flip pass. If the
                // adjoining room has an alternate and we're building flipped pathfinding
                // data, descend into the alternate; otherwise stay on the base side.
                Room adjoiningRoom = sector.FloorPortal.AdjoiningRoom;
                if (adjoiningRoom.AlternateRoom != null && dec_flipped)
                    adjoiningRoom = adjoiningRoom.AlternateRoom;

                // Stop at water boundary (don't cross water/land transition via floor portals)
                if (room.Properties.Type == RoomType.Water != (adjoiningRoom.Properties.Type == RoomType.Water))
                    break;

                dec_room = adjoiningRoom;

                room = dec_room;

                localX = x - room.Position.X;
                localZ = z - room.Position.Z;

                sector = room.Sectors[localX, localZ];
            }

            return true;
        }

        /// <summary>
        /// Checks if a world position is outside the current room bounds.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private bool Dec_NeedsClamping(int x, int z)
        {
            Room room = dec_room;
            return x < 0 || z < 0 || x > room.NumXSectors - 1 || z > room.NumZSectors - 1;
        }

        /// <summary>
        /// Gets floor height at a world position (simplified overload).
        /// </summary>
        [MethodImplAttribute(MethodImplOptions.AggressiveOptimization)]
        private int Dec_GetHeight(int x, int z)
        {
            bool slope;
            return Dec_GetHeight(x, z, out slope);
        }

        /// <summary>
        /// Gets floor height and slope status at a world position.
        ///
        /// This function calculates the average floor height at a sector and determines
        /// if the sector is a slope (too steep for most enemies to walk on).
        ///
        /// FLOOR HEIGHT CALCULATION:
        /// =========================
        /// The floor height is the average of the 4 corner heights plus the room's Y position.
        /// Ghost blocks override actual geometry if present.
        ///
        /// SLOPE DETECTION:
        /// ================
        /// A sector is considered a slope if:
        /// - 3 or more edges have gradient >= 3 clicks (768 world units)
        /// - Opposite edges both have steep gradients
        /// - Adjacent edges have steep gradients (based on split type)
        ///
        /// SIDE EFFECTS:
        /// =============
        /// This function sets several global state variables:
        /// - dec_cornerHeight1-4: Corner heights for the sector
        /// - dec_monkey: True if sector has monkey swing flag
        /// - dec_splitter: Set to true if SPLITTER encountered
        /// - dec_checkUnderwater: Set to false if shallow water detected
        /// - dec_room: Updated when traversing through portals
        /// - dec_doorCheck: Set when crossing room boundaries
        /// </summary>
        /// <param name="x">World X coordinate (in sectors)</param>
        /// <param name="z">World Z coordinate (in sectors)</param>
        /// <param name="slope">Output: true if sector is too steep for walking</param>
        /// <returns>Floor height, or _noHeight if unwalkable</returns>
        [MethodImplAttribute(MethodImplOptions.AggressiveOptimization)]
        private int Dec_GetHeight(int x, int z, out bool slope)
        {
            slope = false;

            Room adjoiningRoom = dec_room;
            Room room = dec_room;

            // Ignore pathfinding for current room?
            if (dec_room.Properties.FlagExcludeFromPathFinding)
                return _noHeight;

            int localX = x - room.Position.X;
            int localZ = z - room.Position.Z;

            // If out of bounds, return no height
            if (localX < 0 ||
                localX > room.NumXSectors - 1 ||
                localZ < 0 ||
                localZ > room.NumZSectors - 1)
            {
                return _noHeight;
            }

            Sector sector = room.Sectors[localX, localZ];

            // If sector is a wall or is a vertical toggle opacity 1
            // Note that is & 8 because wall and border wall are the only sectors with bit 4 (0x08) set
            if ((sector.Type == SectorType.Wall ||
                 sector.Type == SectorType.BorderWall) && (sector.WallPortal == null ||
                sector.WallPortal != null && sector.WallPortal.Opacity == PortalOpacity.SolidFaces) ||
                (sector.Flags & SectorFlags.NotWalkableFloor) != 0)
            {
                dec_cornerHeight1 = Clicks.ToWorld(-1);
                dec_cornerHeight2 = Clicks.ToWorld(-1);
                dec_cornerHeight3 = Clicks.ToWorld(-1);
                dec_cornerHeight4 = Clicks.ToWorld(-1);

                return _noHeight;
            }

            // If it's not a wall portal or is vertical toggle opacity 1
            if (sector.WallPortal is not null && sector.WallPortal.Opacity != PortalOpacity.SolidFaces)
            {
                // Pick the correct side of the adjoining flip pair for this pass. During
                // the flipped pass (dec_flipped == true) we want to read the alternate of
                // the neighbour so that heights observed across a wall portal at a
                // changed/unchanged junction reflect the same flip state that the
                // surrounding box was generated in. Reading the base when dec_flipped is
                // true would inject stale geometry at the junction.
                adjoiningRoom = sector.WallPortal.AdjoiningRoom;
                if (adjoiningRoom.AlternateRoom != null && dec_flipped)
                    adjoiningRoom = adjoiningRoom.AlternateRoom;

                dec_room = adjoiningRoom;
                dec_doorCheck = true;

                room = dec_room;

                localX = x - room.Position.X;
                localZ = z - room.Position.Z;

                sector = room.Sectors[localX, localZ];
            }

            Room oldRoom = dec_room; 

            var connInfo = room.GetFloorRoomConnectionInfo(new VectorInt2(localX, localZ));
            while (sector.FloorPortal != null && connInfo.TraversableType != Room.RoomConnectionType.NoPortal)
            {
                // Same flip-aware resolution as the wall-portal branch above. Without
                // this, descending through a floor portal during the flipped pass would
                // sample heights from the base of an alternated room below, producing
                // an inconsistent floor value at the very sector that should connect
                // changed and unchanged geometry.
                adjoiningRoom = sector.FloorPortal.AdjoiningRoom;
                if (adjoiningRoom.AlternateRoom != null && dec_flipped)
                    adjoiningRoom = adjoiningRoom.AlternateRoom;

                if (sector.FloorPortal.Opacity == PortalOpacity.SolidFaces)
                {
                    if (!((room.Properties.Type == RoomType.Water) ^ (adjoiningRoom.Properties.Type == RoomType.Water)))
                        break;
                }

                dec_room = adjoiningRoom;
                room = dec_room;

                localX = x - room.Position.X;
                localZ = z - room.Position.Z;

                sector = room.Sectors[localX, localZ];
                connInfo = room.GetFloorRoomConnectionInfo(new VectorInt2(localX, localZ));
            }

            if ((sector.Flags & SectorFlags.NotWalkableFloor) != 0) 
                return _noHeight;

            int floorXnZp = sector.HasGhostBlock ? sector.GhostBlock.Floor.XnZp : sector.Floor.XnZp,
                floorXpZp = sector.HasGhostBlock ? sector.GhostBlock.Floor.XpZp : sector.Floor.XpZp,
                floorXpZn = sector.HasGhostBlock ? sector.GhostBlock.Floor.XpZn : sector.Floor.XpZn,
                floorXnZn = sector.HasGhostBlock ? sector.GhostBlock.Floor.XnZn : sector.Floor.XnZn;

            int sumHeights = floorXnZp + floorXpZp + floorXpZn + floorXnZn;
            int tilt = sumHeights / 4;

            dec_cornerHeight1 = floorXnZp;
            dec_cornerHeight2 = floorXpZp;
            dec_cornerHeight3 = floorXpZn;
            dec_cornerHeight4 = floorXnZn;

            int grad1 = Math.Abs(dec_cornerHeight1 - dec_cornerHeight2) >= Clicks.ToWorld(3) ? 1 : 0;
            int grad2 = Math.Abs(dec_cornerHeight2 - dec_cornerHeight3) >= Clicks.ToWorld(3) ? 1 : 0;
            int grad3 = Math.Abs(dec_cornerHeight3 - dec_cornerHeight4) >= Clicks.ToWorld(3) ? 1 : 0;
            int grad4 = Math.Abs(dec_cornerHeight4 - dec_cornerHeight1) >= Clicks.ToWorld(3) ? 1 : 0;

            int type;

            if (floorXnZp == floorXpZn)
                type = 0;
            else if (floorXpZp == floorXnZn)
                type = 1;
            else if (floorXnZp < floorXpZp && floorXnZp < floorXnZn ||
                        floorXpZn < floorXpZp && floorXpZn < floorXnZn ||
                        floorXnZp > floorXpZp && floorXnZp > floorXnZn ||
                        floorXpZn > floorXpZp && floorXpZn > floorXnZn)
                type = 1;
            else
                type = 0;

            int height = tilt + room.Position.Y;

            int ceiling = sector.HasGhostBlock
                ? sector.GhostBlock.Ceiling.Max + room.Position.Y
                : sector.Ceiling.Max + room.Position.Y;

            int delta = ceiling - height;

            // Check for shallow water
            if (dec_checkUnderwater && room.Properties.Type == RoomType.Water && delta <= Clicks.ToWorld(2) && sector.CeilingPortal != null)
            {
                adjoiningRoom = sector.CeilingPortal.AdjoiningRoom;
                if (adjoiningRoom.AlternateRoom != null && dec_flipped) 
                    adjoiningRoom = adjoiningRoom.AlternateRoom;

                if (adjoiningRoom.Properties.Type != RoomType.Water)
                {
                    dec_checkUnderwater = delta > Clicks.ToWorld(1);
                    dec_shallowWater = true;
                }
            }

            dec_room = oldRoom;

            if ((grad1 + grad2 + grad3 + grad4 >= 3 || 
                (grad1 + grad3 == 2) || 
                (grad2 + grad4 == 2) || 
                (type == 0 && ((grad1 + grad4 == 2) || (grad2 + grad3 == 2))) || 
                (type == 1 && ((grad1 + grad2 == 2) || (grad3 + grad4 == 2)))) && 
                dec_checkUnderwater && room.Properties.Type != RoomType.Water)
            {
                slope = true;
                // return height
            }

            if ((sector.Flags & SectorFlags.Box) != 0)
            {
                if (dec_splitter)
                    return _noHeight;
                else
                    dec_splitter = true;
            }

            if ((sector.Flags & SectorFlags.Monkey) != 0)
            {
                dec_monkey = true;
                dec_monkeyCeiling = ceiling;
            }
            else
            {
                dec_monkey = false;
                dec_monkeyCeiling = _noMonkeyCeiling;
            }

            return height;
        }

        /// <summary>
        /// Tests if two boxes can be connected via a jump in the Z direction.
        ///
        /// JUMP DETECTION:
        /// ===============
        /// Enemies like skeletons and humans can jump across 1-2 sector gaps.
        /// This function checks if the gap between boxes is jumpable.
        ///
        /// Requirements for a valid jump:
        /// 1. Boxes must have the same floor height
        /// 2. Gap must be exactly 1 or 2 sectors wide
        /// 3. Gap floor must be at least 2 clicks below the box floor (room to jump)
        /// 4. Gap must be walkable (not a wall or solid portal)
        ///
        /// COORDINATE MAPPING:
        /// ==================
        /// Original: TestTBJumpOverlap (Top-Bottom = Y direction)
        /// TombEditor: Dec_TestJumpOverlapX (tests Z direction due to axis swap)
        /// </summary>
        /// <param name="test">First box to test</param>
        /// <param name="box">Second box to test</param>
        /// <returns>True if boxes can be connected via jump in Z direction</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private bool Dec_TestJumpOverlapX(dec_TombEngine_box_aux test, dec_TombEngine_box_aux box)
        {
            // Boxes must have the same height for jump
            if (test.Height != box.Height)
                return false;

            int xMin = test.Xmin > box.Xmin ? test.Xmin : box.Xmin;
            int xMax = test.Xmax < box.Xmax ? test.Xmax : box.Xmax;

            int zMin = test.Zmin;
            int zMax = box.Zmax;

            int currentX = (xMin + xMax) >> 1;

            int floor = 0;

            // 1 sector jump
            if (zMax == zMin - 1)
            {
                dec_room = Dec_ResolveRoomForFlipPass(box.Room);

                if (!Dec_ClampRoom(currentX, zMax - 1))
                    return false;

                if (!Dec_ClampRoom(currentX, zMax))
                    return false;

                floor = Dec_GetHeight(currentX, zMax);
                if (floor > box.Height - Clicks.ToWorld(2) || floor == _noHeight)
                    return false;

                return true;
            }

            // 2 sectors jump
            if (zMax == zMin - 2)
            {
                dec_room = Dec_ResolveRoomForFlipPass(box.Room);

                if (!Dec_ClampRoom(currentX, zMax - 1))
                    return false;

                if (!Dec_ClampRoom(currentX, zMax))
                    return false;

                floor = Dec_GetHeight(currentX, zMax);
                if (floor > box.Height - Clicks.ToWorld(2) || floor == _noHeight)
                    return false;

                if (!Dec_ClampRoom(currentX, zMax + 1))
                    return false;

                floor = Dec_GetHeight(currentX, zMax + 1);
                if (floor > box.Height - Clicks.ToWorld(2) || floor == _noHeight)
                    return false;

                return true;
            }

            // Swap cases

            zMin = box.Zmin;
            zMax = test.Zmax;

            // 1 sector jump
            if (zMax == zMin - 1)
            {
                dec_room = Dec_ResolveRoomForFlipPass(box.Room);

                if (!Dec_ClampRoom(currentX, zMax - 1))
                    return false;

                if (!Dec_ClampRoom(currentX, zMax))
                    return false;

                floor = Dec_GetHeight(currentX, zMax);
                if (floor > box.Height - Clicks.ToWorld(2) || floor == _noHeight)
                    return false;

                return true;
            }

            // 2 sectors jump
            if (zMax == zMin - 2)
            {
                dec_room = Dec_ResolveRoomForFlipPass(box.Room);

                if (!Dec_ClampRoom(currentX, zMax - 1))
                    return false;

                if (!Dec_ClampRoom(currentX, zMax))
                    return false;

                floor = Dec_GetHeight(currentX, zMax);
                if (floor > box.Height - Clicks.ToWorld(2) || floor == _noHeight)
                    return false;

                if (!Dec_ClampRoom(currentX, zMax + 1))
                    return false;

                floor = Dec_GetHeight(currentX, zMax + 1);
                if (floor > box.Height - Clicks.ToWorld(2) || floor == _noHeight)
                    return false;

                return true;
            }

            return false;
        }

        /// <summary>
        /// Tests if two boxes can be connected via a jump in the X direction.
        ///
        /// See Dec_TestJumpOverlapX for detailed documentation.
        /// This function tests the X direction (left-right) while that one tests Z.
        ///
        /// COORDINATE MAPPING:
        /// ==================
        /// Original: TestLRJumpOverlap (Left-Right = X direction)
        /// TombEditor: Dec_TestJumpOverlapZ (tests X direction, name is historical)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private bool Dec_TestJumpOverlapZ(dec_TombEngine_box_aux a, dec_TombEngine_box_aux b)
        {
            // Boxes must have the same height for jump
            if (a.Height != b.Height)
                return false;

            int zMin = a.Zmin > b.Zmin ? a.Zmin : b.Zmin;
            int zMax = a.Zmax < b.Zmax ? a.Zmax : b.Zmax;

            int xMin = a.Xmin;
            int xMax = b.Xmax;

            int currentZ = (zMin + zMax) >> 1;

            int floor = 0;

            // 1 sector jump
            if (xMax == xMin - 1)
            {
                dec_room = Dec_ResolveRoomForFlipPass(b.Room);

                if (!Dec_ClampRoom(xMax - 1, currentZ))
                    return false;

                if (!Dec_ClampRoom(xMax, currentZ))
                    return false;

                floor = Dec_GetHeight(xMax, currentZ);
                if (floor > b.Height - Clicks.ToWorld(2) || floor == _noHeight)
                    return false;

                return true;
            }

            // 2 sectors jump
            if (xMax == xMin - 2)
            {
                dec_room = Dec_ResolveRoomForFlipPass(b.Room);

                if (!Dec_ClampRoom(xMax - 1, currentZ))
                    return false;

                if (!Dec_ClampRoom(xMax, currentZ))
                    return false;

                floor = Dec_GetHeight(xMax, currentZ);
                if (floor > b.Height - Clicks.ToWorld(2) || floor == _noHeight)
                    return false;

                if (!Dec_ClampRoom(xMax + 1, currentZ))
                    return false;

                floor = Dec_GetHeight(xMax + 1, currentZ);
                if (floor > b.Height - Clicks.ToWorld(2) || floor == _noHeight)
                    return false;

                return true;
            }

            // Swap cases
            xMin = b.Xmin;
            xMax = a.Xmax;

            // 1 sector jump
            if (xMax == xMin - 1)
            {
                dec_room = Dec_ResolveRoomForFlipPass(b.Room);

                if (!Dec_ClampRoom(xMax - 1, currentZ))
                    return false;

                if (!Dec_ClampRoom(xMax, currentZ))
                    return false;

                floor = Dec_GetHeight(xMax, currentZ);
                if (floor > b.Height - Clicks.ToWorld(2) || floor == _noHeight)
                    return false;

                return true;
            }

            // 2 sectors jump
            if (xMax == xMin - 2)
            {
                dec_room = Dec_ResolveRoomForFlipPass(b.Room);

                if (!Dec_ClampRoom(xMax - 1, currentZ))
                    return false;

                if (!Dec_ClampRoom(xMax, currentZ))
                    return false;

                floor = Dec_GetHeight(xMax, currentZ);
                if (floor > b.Height - Clicks.ToWorld(2) || floor == _noHeight)
                    return false;

                if (!Dec_ClampRoom(xMax + 1, currentZ))
                    return false;

                floor = Dec_GetHeight(xMax + 1, currentZ);
                if (floor > b.Height - Clicks.ToWorld(2) || floor == _noHeight)
                    return false;

                return true;
            }

            return false;
        }

        /// <summary>
        /// Tests edge adjacency when test.Xmax touches box (right edge overlap).
        ///
        /// EDGE ADJACENCY:
        /// ===============
        /// Two boxes are adjacent if they share an edge (not just a corner).
        /// This function verifies that the shared edge has matching floor heights.
        ///
        /// Tests all sectors along the shared edge to ensure they connect properly.
        /// </summary>
        // Resolves a box.Room reference to the alternate version when we're in
        // the flipped pass. This is critical for boxes that were merged via
        // Dec_AddBox: the merge keeps the Room field from whichever pass first
        // added the box (typically Pass 0 = base). Without this re-resolution,
        // Pass 2 calls to Dec_ClampRoom / Dec_GetHeight using `dec_room = box.Room`
        // read BASE geometry even when sampling for an alt-pass adjacency check.
        // That lets Pass 2 accept overlaps where alt geometry actually walls them
        // off -- exactly the "BFS routes through alt block" bug seen with merged
        // upper-room boxes whose alt counterparts have raised floors.
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private Room Dec_ResolveRoomForFlipPass(Room r)
        {
            if (dec_flipped && r != null && r.AlternateRoom != null)
                return r.AlternateRoom;
            return r;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private bool Dec_TestMonkeySwingOverlap(dec_TombEngine_box_aux box1, dec_TombEngine_box_aux box2)
        {
            if (!box1.Monkey || !box2.Monkey)
                return false;

            if (Math.Abs(box1.MonkeyCeiling - box2.MonkeyCeiling) > _monkeySwingCeilingLimit)
                return false;

            bool hasXOverlap = box1.Xmax > box2.Xmin && box1.Xmin < box2.Xmax;
            bool hasZOverlap = box1.Zmax > box2.Zmin && box1.Zmin < box2.Zmax;

            if (hasXOverlap && hasZOverlap)
                return true;

            return hasXOverlap && (box1.Zmax == box2.Zmin || box1.Zmin == box2.Zmax) ||
                   hasZOverlap && (box1.Xmax == box2.Xmin || box1.Xmin == box2.Xmax);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool Dec_TestOverlapXmax(dec_TombEngine_box_aux test, dec_TombEngine_box_aux box)
        {
            int startZ = test.Zmin > box.Zmin ? test.Zmin : box.Zmin;
            int endZ = test.Zmax < box.Zmax ? test.Zmax : box.Zmax;

            Room testRoom = Dec_ResolveRoomForFlipPass(test.Room);
            Room boxRoom  = Dec_ResolveRoomForFlipPass(box.Room);

            for (int z = startZ; z < endZ; z++)
            {
                // Clamp for the test (own) side first so dec_doorCheck/splitter side
                // effects mirror the original behaviour.
                dec_room = testRoom;

                if (!Dec_ClampRoom(test.Xmax - 1, z))
                    return false;

                // PORTAL-CONNECTIVITY CHECK: traverse wall portals from
                // test.Room toward the box-side sector. If there is no real
                // portal chain connecting them at this boundary, Dec_ClampRoom
                // returns false and we reject the overlap. Without this guard
                // two rooms whose world XZ rectangles merely touch (no portal
                // between them) get a "phantom" overlap and BFS paths through
                // walls.
                if (test.Room != box.Room)
                {
                    dec_room = testRoom;
                    if (!Dec_ClampRoom(test.Xmax, z))
                        return false;

                    // FLOOR-CONTINUITY CHECK (water phantom-wall fix):
                    // Dec_ClampRoom can "leak" -- if the two rooms share a portal
                    // ANYWHERE along the boundary, the clamp reaches the box-side room
                    // even when THIS specific edge sector is a wall. The box-side height
                    // sample below then tautologically matches box.Height, so a phantom
                    // overlap through the wall is accepted (e.g. a shallow shelf box
                    // edge-touching a deep trench box 11 blocks below in another room).
                    // Sample the floor as reached FROM THE TEST ROOM and require it
                    // equals box.Height: a real edge connection has a continuous floor
                    // (clamp traverses a true portal and lands on the box floor), while
                    // a wall yields the test room's own floor or _noHeight -> mismatch.
                    // Gated to WET boxes (deep OR shallow water) where the swim/amphibious
                    // Step/Drop lets BFS exploit these. Shallow must be included: a shallow
                    // shore box edge-touching a deep box of a room BELOW the floor slipped
                    // through the old Water&&Water gate, and the Wet<->Wet amphibious flag
                    // then legalized a route straight through solid floor.
                    if ((test.Water || test.Shallow) && (box.Water || box.Shallow))
                    {
                        dec_splitter = false;
                        bool seamContinuous = box.Height == Dec_GetHeight(test.Xmax, z);

                        // STACKED-WATER RESCUE (mutual continuity). When the test box's
                        // floor has collapsed through a vertical portal into a LOWER room,
                        // test.Room IS that lower room, and sampling the box-side sector
                        // "from the test room" reads the lower room's geometry -- which
                        // wrongly rejects a legit swim seam (e.g. a shore ramp bordering
                        // deep stacked water: the creature really can swim over the ramp,
                        // and in the flip state without the stack the same seam passes
                        // because test.Room == box.Room skips this check entirely). Probe
                        // the opposite direction too: the TEST-side seam sector as seen
                        // from the BOX room. In the legit stacked case the box room's
                        // water column descends through the vertical portal onto the test
                        // box's floor (heights match); across a solid floor/wall (phantom
                        // seam) BOTH directions mismatch and the overlap stays rejected.
                        if (!seamContinuous)
                        {
                            dec_room = boxRoom;
                            if (Dec_ClampRoom(test.Xmax - 1, z))
                            {
                                dec_splitter = false;
                                seamContinuous = test.Height == Dec_GetHeight(test.Xmax - 1, z);
                            }
                        }

                        if (!seamContinuous)
                            return false;
                    }
                }

                // FIX: Re-resolve which room actually owns the box-side sector before
                // sampling its floor height.
                dec_room = boxRoom;
                if (!Dec_ClampRoom(test.Xmax, z))
                    return false;

                dec_splitter = false;

                if (box.Height != Dec_GetHeight(test.Xmax, z))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Tests edge adjacency when test.Xmin touches box (left edge overlap).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool Dec_TestOverlapXmin(dec_TombEngine_box_aux test, dec_TombEngine_box_aux box)
        {
            int startZ = test.Zmin > box.Zmin ? test.Zmin : box.Zmin;
            int endZ = test.Zmax < box.Zmax ? test.Zmax : box.Zmax;

            Room testRoom = Dec_ResolveRoomForFlipPass(test.Room);
            Room boxRoom  = Dec_ResolveRoomForFlipPass(box.Room);

            for (int z = startZ; z < endZ; z++)
            {
                dec_room = testRoom;

                if (!Dec_ClampRoom(test.Xmin, z))
                    return false;

                // PORTAL-CONNECTIVITY CHECK: see Dec_TestOverlapXmax.
                if (test.Room != box.Room)
                {
                    dec_room = testRoom;
                    if (!Dec_ClampRoom(test.Xmin - 1, z))
                        return false;

                    // FLOOR-CONTINUITY CHECK (water phantom-wall fix) with the
                    // STACKED-WATER RESCUE (mutual continuity): see Dec_TestOverlapXmax.
                    if ((test.Water || test.Shallow) && (box.Water || box.Shallow))
                    {
                        dec_splitter = false;
                        bool seamContinuous = box.Height == Dec_GetHeight(test.Xmin - 1, z);

                        if (!seamContinuous)
                        {
                            dec_room = boxRoom;
                            if (Dec_ClampRoom(test.Xmin, z))
                            {
                                dec_splitter = false;
                                seamContinuous = test.Height == Dec_GetHeight(test.Xmin, z);
                            }
                        }

                        if (!seamContinuous)
                            return false;
                    }
                }

                // FIX: see Dec_TestOverlapXmax. Resolve the box-side room for the
                // sector that lies one step past test.Xmin in the -X direction so
                // the height sample reads from the actual neighbouring room rather
                // than out-of-bounds in test.Room.
                dec_room = boxRoom;
                if (!Dec_ClampRoom(test.Xmin - 1, z))
                    return false;

                dec_splitter = false;

                if (box.Height != Dec_GetHeight(test.Xmin - 1, z))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Tests edge adjacency when test.Zmax touches box (top edge overlap).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool Dec_TestOverlapZmax(dec_TombEngine_box_aux test, dec_TombEngine_box_aux box)
        {
            int startX = test.Xmin > box.Xmin ? test.Xmin : box.Xmin;
            int endX = test.Xmax < box.Xmax ? test.Xmax : box.Xmax;

            Room testRoom = Dec_ResolveRoomForFlipPass(test.Room);
            Room boxRoom  = Dec_ResolveRoomForFlipPass(box.Room);

            for (int x = startX; x < endX; x++)
            {
                dec_room = testRoom;

                if (!Dec_ClampRoom(x, test.Zmax - 1))
                    return false;

                // PORTAL-CONNECTIVITY CHECK: see Dec_TestOverlapXmax.
                if (test.Room != box.Room)
                {
                    dec_room = testRoom;
                    if (!Dec_ClampRoom(x, test.Zmax))
                        return false;

                    // FLOOR-CONTINUITY CHECK (water phantom-wall fix) with the
                    // STACKED-WATER RESCUE (mutual continuity): see Dec_TestOverlapXmax.
                    if ((test.Water || test.Shallow) && (box.Water || box.Shallow))
                    {
                        dec_splitter = false;
                        bool seamContinuous = box.Height == Dec_GetHeight(x, test.Zmax);

                        if (!seamContinuous)
                        {
                            dec_room = boxRoom;
                            if (Dec_ClampRoom(x, test.Zmax - 1))
                            {
                                dec_splitter = false;
                                seamContinuous = test.Height == Dec_GetHeight(x, test.Zmax - 1);
                            }
                        }

                        if (!seamContinuous)
                            return false;
                    }
                }

                // FIX: see Dec_TestOverlapXmax. Resolve the box-side room for the
                // sector at (x, test.Zmax) -- otherwise cross-room edge adjacency
                // is silently rejected (Dec_GetHeight reads out-of-bounds in
                // test.Room and returns _noHeight).
                dec_room = boxRoom;
                if (!Dec_ClampRoom(x, test.Zmax))
                    return false;

                dec_splitter = false;

                if (box.Height != Dec_GetHeight(x, test.Zmax))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Tests edge adjacency when test.Zmin touches box (bottom edge overlap).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool Dec_TestOverlapZmin(dec_TombEngine_box_aux test, dec_TombEngine_box_aux box)
        {
            int startX = test.Xmin > box.Xmin ? test.Xmin : box.Xmin;
            int endX = test.Xmax < box.Xmax ? test.Xmax : box.Xmax;

            Room testRoom = Dec_ResolveRoomForFlipPass(test.Room);
            Room boxRoom  = Dec_ResolveRoomForFlipPass(box.Room);

            for (int x = startX; x < endX; x++)
            {
                dec_room = testRoom;

                if (!Dec_ClampRoom(x, test.Zmin))
                    return false;

                // PORTAL-CONNECTIVITY CHECK: see Dec_TestOverlapXmax.
                if (test.Room != box.Room)
                {
                    dec_room = testRoom;
                    if (!Dec_ClampRoom(x, test.Zmin - 1))
                        return false;

                    // FLOOR-CONTINUITY CHECK (water phantom-wall fix) with the
                    // STACKED-WATER RESCUE (mutual continuity): see Dec_TestOverlapXmax.
                    if ((test.Water || test.Shallow) && (box.Water || box.Shallow))
                    {
                        dec_splitter = false;
                        bool seamContinuous = box.Height == Dec_GetHeight(x, test.Zmin - 1);

                        if (!seamContinuous)
                        {
                            dec_room = boxRoom;
                            if (Dec_ClampRoom(x, test.Zmin))
                            {
                                dec_splitter = false;
                                seamContinuous = test.Height == Dec_GetHeight(x, test.Zmin);
                            }
                        }

                        if (!seamContinuous)
                            return false;
                    }
                }

                // FIX: see Dec_TestOverlapXmax. Resolve the box-side room for the
                // sector at (x, test.Zmin - 1) before sampling its floor height,
                // otherwise cross-room edge adjacency is silently rejected.
                dec_room = boxRoom;
                if (!Dec_ClampRoom(x, test.Zmin - 1))
                    return false;

                dec_splitter = false;

                if (box.Height != Dec_GetHeight(x, test.Zmin - 1))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Main overlap check between two boxes.
        ///
        /// OVERLAP TYPES:
        /// ==============
        /// 1. STRAIGHT OVERLAP: Boxes overlap in both X and Z (must have same height)
        /// 2. EDGE ADJACENCY: Boxes share an edge (adjacent but not overlapping)
        /// 3. JUMP OVERLAP: Boxes separated by 1-2 sector gap (jumpable)
        ///
        /// OUTPUT FLAGS (set as side effects):
        /// - dec_jump: True if connection requires jumping
        /// - dec_monkey: True if both boxes have monkey swing ceilings
        ///
        /// ALGORITHM:
        /// ==========
        /// 1. Always process from higher to lower box (swap if needed)
        /// 2. Check X overlap first:
        ///    - If no X overlap: try X-direction jump or edge adjacency
        ///    - If X overlap: check Z overlap (straight) or try Z-direction jump or edge adjacency
        /// 3. Set monkey flag if both boxes have monkey swing
        ///
        /// </summary>
        /// <param name="a">First box to test</param>
        /// <param name="b">Second box to test</param>
        /// <returns>True if boxes overlap/connect, false otherwise</returns>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private bool Dec_CheckOverlap(dec_TombEngine_box_aux a, dec_TombEngine_box_aux b)
        {
            dec_jump = false;
            dec_monkey = false;

            if (Dec_TestMonkeySwingOverlap(a, b))
            {
                dec_monkey = true;
                return true;
            }

            // Always process from higher to lower box (numerically higher Height value)
            dec_TombEngine_box_aux box1 = a;
            dec_TombEngine_box_aux box2 = b;

            if (b.Height > a.Height)
            {
                box1 = b;
                box2 = a;
            }

            bool hasXOverlap = box1.Xmax > box2.Xmin && box1.Xmin < box2.Xmax;
            bool hasZOverlap = box1.Zmax > box2.Zmin && box1.Zmin < box2.Zmax;

            // ===================================================================================
            // CASE 1: No X overlap - check for X-direction connections
            // ===================================================================================
            if (!hasXOverlap)
            {
                // Try X-direction jump if Z ranges overlap
                if (hasZOverlap && Dec_TestJumpOverlapZ(box1, box2))
                {
                    dec_jump = true;
                    return true;
                }

                // Edge adjacency requires Z overlap
                if (!hasZOverlap)
                    return false;

                // Check X edge adjacency
                if (box1.Xmax == box2.Xmin)
                {
                    if (!Dec_TestOverlapXmax(box1, box2))
                        return false;
                }
                else if (box1.Xmin == box2.Xmax)
                {
                    if (!Dec_TestOverlapXmin(box1, box2))
                        return false;
                }
                else
                {
                    return false;
                }

                if (box1.Monkey && box2.Monkey) dec_monkey = true;
                return true;
            }

            // ===================================================================================
            // CASE 2: X overlap and Z overlap - straight overlap
            // ===================================================================================
            if (hasZOverlap)
            {
                // Straight overlap - must have same height
                if (box1.Height != box2.Height)
                    return false;

                if (box1.Monkey && box2.Monkey) dec_monkey = true;
                return true;
            }

            // ===================================================================================
            // CASE 3: X overlap but no Z overlap - check for Z-direction connections
            // ===================================================================================
            // Try Z-direction jump
            if (Dec_TestJumpOverlapX(box2, box1))
            {
                dec_jump = true;
                return true;
            }

            // Check Z edge adjacency
            if (box1.Zmax == box2.Zmin)
            {
                if (!Dec_TestOverlapZmax(box1, box2))
                    return false;
            }
            else if (box1.Zmin == box2.Zmax)
            {
                if (!Dec_TestOverlapZmin(box1, box2))
                    return false;
            }
            else
            {
                return false;
            }

            if (box1.Monkey && box2.Monkey) dec_monkey = true;
            return true;
        }
    }
}
