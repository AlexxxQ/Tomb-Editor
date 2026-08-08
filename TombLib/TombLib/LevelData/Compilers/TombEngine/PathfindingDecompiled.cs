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
     * - Zone: Runtime connectivity groups rebuilt by the engine for the active flip state
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
        // Bit 0 is the normal room state; bit 1 is the alternate room state.
        // Non-flipped rooms exist in both states.
        public byte RoomStateMask;

        // =========================================================================================
        // ROOM REFERENCE
        // =========================================================================================
        public Room Room;          // Geometry room after floor portal traversal.
        public Room SectorRoom;    // Room whose sector stores this box index.
        public readonly HashSet<Room> SectorRooms = new HashSet<Room>();
        public int FloorFlipDependencyGroup = -1;
        public byte FloorFlipDependencyState;
        public int FloorFlipCounterpartSignature = int.MinValue;
        public int VerticalPortalSignature;
    }

    /// <summary>
    /// Partial class containing box and overlap generation code.
    /// </summary>
    public sealed partial class LevelCompilerTombEngine
    {
        private const byte RoomStateUnflipped = 1;
        private const byte RoomStateFlipped = 2;

        public class BoxFlags
        {
            public const int Water		= 0x0200;
            public const int Shallow	= 0x0400;
            public const int Slope		= 0x0800;  // Steep floor (gradient >= 3 clicks). Exported so the
                                                   // engine's runtime zone re-flood can reproduce the land
                                                   // slope filter (which is otherwise compile-time only).
            public const int Blocked	= 0x4000;
            public const int Splitter	= 0x8000;

        }

        public class OverlapFlags
        {
            public const int FlyerOnly = 0x0001;
            public const int RouteExitFloorHint = 0x0004;
            // Per-edge flip-group conditions use otherwise unused overlap bits.
            public const int PairStateMaskShift = 3;
            public const int PairStateMask = 0x0078;
            public const int PairStateValidity = 0x0080;
            public const int PairSourceGroupValidity = 0x0100;
            public const int PairTargetGroupValidity = 0x0200;
            public const int PairSourceGroupShift = 16;
            public const int PairSourceGroupMask = 0x00FF0000;
            public const int PairTargetGroupShift = 24;
            public const int PairTargetGroupMask = unchecked((int)0xFF000000);

            public const int Jump = 0x0800;
            public const int ShallowWaterLowEdge = 0x1000;
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
        private Room dec_floorRoom; // Actual floor room after vertical portal traversal.

        /// <summary>
        /// Flag set by Dec_GetHeight when a MONKEY is encountered.
        /// Indicates the sector has a monkey swing ceiling.
        /// Boxes with monkey swing get the MONKEY_BOX_FLAG.
        /// </summary>
        private bool dec_monkey;
        private bool dec_flyerOnly;

        /// <summary>
        /// Current flip state being processed.
        /// False = processing normal rooms, True = processing flipped rooms.
        /// </summary>
        private bool dec_flipped;

        // Group-state overrides for mixed flipmap checks.
        private Dictionary<int, bool> dec_flipGroupOverrides;

        /// <summary>
        /// Flag set by Dec_CheckOverlap when boxes are connected via a jump.
        /// Used to set the JUMP_BIT (0x0800) flag on the overlap.
        /// </summary>
        private bool dec_jump;

        // Marks links whose runtime floor probe must use the route exit box.
        private bool dec_routeExitFloorHint;

        // Set while sampling an edge through a vertical portal.
        private bool dec_overlapTraversedVerticalPortal;

        private int dec_boxSourceFlipGroup;
        private int dec_floorFlipDependencyGroup = -1;
        private byte dec_floorFlipDependencyState;
        private int dec_floorFlipCounterpartSignature = int.MinValue;
        private int dec_boxFloorCounterpartSignature = int.MinValue;
        private int dec_verticalPortalSignature;
        private int dec_boxVerticalPortalSignature;

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
            Dec_BuildSectorBoxVariants();

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

        private HashSet<int> Dec_GetLocalFlipDependencyGroups(Room room, int x, int z)
        {
            var groups = new HashSet<int>();
            bool savedFlipped = dec_flipped;
            var savedOverrides = dec_flipGroupOverrides;
            Room savedRoom = dec_room;
            int savedSourceGroup = dec_boxSourceFlipGroup;

            dec_flipGroupOverrides = null;
            dec_boxSourceFlipGroup = Dec_GetRoomFlipGroup(room);
            foreach (var offset in new[] { (0, 0), (-1, 0), (1, 0), (0, -1), (0, 1) })
            {
                dec_room = room;
                bool slope;
                Dec_GetHeight(room.Position.X + x + offset.Item1,
                    room.Position.Z + z + offset.Item2, out slope);

                if (dec_floorFlipDependencyGroup >= 0)
                    groups.Add(dec_floorFlipDependencyGroup);
            }

            dec_room = savedRoom;
            dec_boxSourceFlipGroup = savedSourceGroup;
            dec_flipGroupOverrides = savedOverrides;
            dec_flipped = savedFlipped;
            return groups;
        }

        private void Dec_BuildSectorBoxVariants()
        {
            _sectorBoxVariants.Clear();

            bool savedFlipped = dec_flipped;
            var savedOverrides = dec_flipGroupOverrides;
            var variantOverrides = new Dictionary<int, bool>();

            foreach (Room room in _level.Rooms)
            {
                if (room == null || !_tempRooms.TryGetValue(room, out TombEngineRoom tempRoom))
                    continue;

                bool sourceFlipped = room.AlternateBaseRoom != null;
                for (int z = 1; z < room.NumZSectors - 1; z++)
                {
                    for (int x = 1; x < room.NumXSectors - 1; x++)
                    {
                        int sectorIndex = tempRoom.NumZSectors * x + z;
                        int defaultBox = tempRoom.Sectors[sectorIndex].BoxIndex;
                        if (defaultBox < 0 || defaultBox >= dec_boxes.Count)
                            continue;

                        var dependencyGroups = new List<int>(Dec_GetLocalFlipDependencyGroups(room, x, z));
                        dependencyGroups.Sort();
                        if (dependencyGroups.Count == 0)
                            continue;

                        int CompileVariant(int stateMask)
                        {
                            dec_flipped = sourceFlipped;
                            variantOverrides.Clear();
                            dec_flipGroupOverrides = variantOverrides;
                            for (int groupIndex = 0; groupIndex < dependencyGroups.Count; groupIndex++)
                                dec_flipGroupOverrides[dependencyGroups[groupIndex]] =
                                    (stateMask & (1 << groupIndex)) != 0;

                            var variantBox = new dec_TombEngine_box_aux();
                            if (!Dec_GetBox(variantBox, x, z, room))
                                return -1;

                            return Dec_AddBox(variantBox, mergeExisting: false);
                        }

                        var variants = new TombEngineSectorBoxVariants
                        {
                            Room = _roomRemapping[room],
                            Sector = sectorIndex
                        };
                        variants.Groups.AddRange(dependencyGroups);

                        bool hasVariation = false;
                        int stateCount = 1 << dependencyGroups.Count;
                        for (int stateMask = 0; stateMask < stateCount; stateMask++)
                        {
                            int variantBox = CompileVariant(stateMask);
                            variants.Boxes.Add(variantBox);
                            hasVariation |= variantBox != defaultBox;
                        }

                        if (hasVariation)
                            _sectorBoxVariants.Add(variants);
                    }
                }
            }

            dec_flipGroupOverrides = savedOverrides;
            dec_flipped = savedFlipped;
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
        /// RoomStateMask records valid geometry states; independent pairs also test both mixed states.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private bool Dec_BuildOverlaps()
        {
            dec_overlaps = new List<TombEngineOverlap>();

            if (dec_boxes.Count == 0)
                return false;

            int i = 0;
            int j = 0;
            var overlapIndexByTarget = new Dictionary<(int Box, int HeightDelta, bool FlyerOnly), int>();
            var mixedOverrides = new Dictionary<int, bool>(2);

            // ===================================================================================
            // MAIN LOOP: Check all box pairs for overlaps
            // ===================================================================================
            do
            {
                var box1 = dec_boxes[i];
                dec_boxes[i].OverlapIndex = -1;  // Reset overlap index

                int numOverlapsAdded = 0;

                // Merge repeated target-box entries across state passes.
                overlapIndexByTarget.Clear();

                void AddOrMergeOverlap(int targetBoxIndex, bool sourceFlipped, bool targetFlipped,
                    dec_TombEngine_box_aux from, dec_TombEngine_box_aux to)
                {
                    var overlap = Dec_CreateOverlap(targetBoxIndex, sourceFlipped, targetFlipped, from, to);
                    var overlapKey = (targetBoxIndex, overlap.HeightDelta,
                        (overlap.Flags & OverlapFlags.FlyerOnly) != 0);

                    if (overlapIndexByTarget.TryGetValue(overlapKey, out int existingIdx))
                    {
                        var existing = dec_overlaps[existingIdx];
                        existing.Flags |= overlap.Flags & ~OverlapFlags.End;
                        dec_overlaps[existingIdx] = existing;
                    }
                    else
                    {
                        if (dec_boxes[i].OverlapIndex == -1)
                            dec_boxes[i].OverlapIndex = dec_overlaps.Count;

                        overlapIndexByTarget[overlapKey] = dec_overlaps.Count;
                        dec_overlaps.Add(overlap);
                        numOverlapsAdded++;
                    }
                }

                void CheckUniformPass(bool flipped)
                {
                    byte roomState = flipped ? RoomStateFlipped : RoomStateUnflipped;
                    if ((box1.RoomStateMask & roomState) == 0)
                        return;

                    dec_flipped = flipped;
                    j = 0;
                    do
                    {
                        if (i != j)
                        {
                            var box2 = dec_boxes[j];
                            if ((box2.RoomStateMask & roomState) != 0 && Dec_CheckAnyOverlap(box1, box2))
                                AddOrMergeOverlap(j, flipped, flipped, box1, box2);
                        }

                        j++;
                    }
                    while (j < dec_boxes.Count);
                }

                CheckUniformPass(false);
                CheckUniformPass(true);

                // Uniform passes miss seams between independent groups; test both mixed states.
                int mixedSourceGroup = Dec_GetBoxFlipGroup(box1);
                if (mixedSourceGroup >= 0)
                {
                    var oldOverrides = dec_flipGroupOverrides;
                    dec_flipGroupOverrides = mixedOverrides;

                    try
                    {
                        for (int mixedState = 0; mixedState < 2; mixedState++)
                        {
                            bool sourceFlipped = mixedState != 0;
                            bool targetFlipped = !sourceFlipped;
                            dec_flipped = sourceFlipped;
                            j = 0;
                            do
                            {
                                if (i != j)
                                {
                                    var box2 = dec_boxes[j];
                                    int targetGroup = Dec_GetBoxFlipGroup(box2);
                                    if (targetGroup >= 0 && targetGroup != mixedSourceGroup)
                                    {
                                        mixedOverrides.Clear();
                                        mixedOverrides[mixedSourceGroup] = sourceFlipped;
                                        mixedOverrides[targetGroup] = targetFlipped;

                                        if (Dec_BoxExistsForCurrentStates(box1) &&
                                            Dec_BoxExistsForCurrentStates(box2) &&
                                            Dec_CheckAnyOverlap(box1, box2))
                                            AddOrMergeOverlap(j, sourceFlipped, targetFlipped, box1, box2);
                                    }
                                }

                                j++;
                            }
                            while (j < dec_boxes.Count);
                        }
                    }
                    finally
                    {
                        dec_flipGroupOverrides = oldOverrides;
                    }
                }

                i++;

                // Mark end of this box's overlap list with END_BIT
                if (numOverlapsAdded != 0)
                    dec_overlaps[dec_overlaps.Count - 1].Flags |= OverlapFlags.End;  // END_BIT
            }
            while (i < dec_boxes.Count);

            Dec_MarkShallowWaterLowEdges();
            dec_flipped = false;

            return true;
        }

        private void Dec_MarkShallowWaterLowEdges()
        {
            var edges = new List<(int Overlap, int ShallowBox, int Delta)>();
            var lowDeltas = new int[dec_boxes.Count];
            Array.Fill(lowDeltas, int.MaxValue);

            for (int fromBox = 0; fromBox < dec_boxes.Count; fromBox++)
            {
                var from = dec_boxes[fromBox];
                int index = dec_boxes[fromBox].OverlapIndex;
                while (index >= 0 && index < dec_overlaps.Count)
                {
                    var overlap = dec_overlaps[index];
                    var to = dec_boxes[overlap.Box];
                    int shallowBox = from.Shallow && to.Water ? fromBox :
                        (from.Water && to.Shallow ? overlap.Box : -1);

                    if (shallowBox >= 0)
                    {
                        int delta = Math.Abs(overlap.HeightDelta);
                        edges.Add((index, shallowBox, delta));
                        lowDeltas[shallowBox] = Math.Min(lowDeltas[shallowBox], delta);
                    }

                    index++;
                    if ((overlap.Flags & OverlapFlags.End) != 0)
                        break;
                }
            }

            foreach (var edge in edges)
            {
                if (edge.Delta == lowDeltas[edge.ShallowBox])
                    dec_overlaps[edge.Overlap].Flags |= OverlapFlags.ShallowWaterLowEdge;
            }
        }

        private TombEngineOverlap Dec_CreateOverlap(int box, bool sourceFlipped, bool targetFlipped,
            dec_TombEngine_box_aux from, dec_TombEngine_box_aux to)
        {
            var overlap = new TombEngineOverlap
            {
                Box = box,
                Flags = 0,
                HeightDelta = -Dec_GetOverlapHeightDelta(from, to)
            };

            int sourceGroup = Dec_GetBoxFlipGroup(from);
            int targetGroup = Dec_GetBoxFlipGroup(to);
            if (sourceGroup >= 0 || targetGroup >= 0)
            {
                bool sourceState = sourceGroup >= 0 && sourceFlipped;
                bool targetState = targetGroup >= 0 && targetFlipped;
                int state = (sourceState ? 2 : 0) | (targetState ? 1 : 0);
                overlap.Flags |= OverlapFlags.PairStateValidity;
                overlap.Flags |= 1 << (OverlapFlags.PairStateMaskShift + state);

                if (sourceGroup >= 0)
                {
                    overlap.Flags |= OverlapFlags.PairSourceGroupValidity;
                    overlap.Flags |= (sourceGroup << OverlapFlags.PairSourceGroupShift) & OverlapFlags.PairSourceGroupMask;
                }

                if (targetGroup >= 0)
                {
                    overlap.Flags |= OverlapFlags.PairTargetGroupValidity;
                    overlap.Flags |= (targetGroup << OverlapFlags.PairTargetGroupShift) & OverlapFlags.PairTargetGroupMask;
                }
            }

            if (dec_jump)
                overlap.Flags |= OverlapFlags.Jump;
            if (dec_flyerOnly)
                overlap.Flags |= OverlapFlags.FlyerOnly;
            if (dec_monkey)
                overlap.Flags |= OverlapFlags.Monkey;
            if (dec_routeExitFloorHint)
                overlap.Flags |= OverlapFlags.RouteExitFloorHint;

            bool bothAquatic = (from.Water || from.Shallow) && (to.Water || to.Shallow);
            bool exitsShallowToDry = from.Shallow && !to.Water && !to.Shallow;
            bool traversableStep = Math.Abs(overlap.HeightDelta) <= Clicks.ToWorld(1) &&
                (!exitsShallowToDry || overlap.HeightDelta == 0);
            if (bothAquatic || traversableStep)
                overlap.Flags |= OverlapFlags.AmphibiousTraversable;

            return overlap;
        }

        private static int Dec_GetRoomFlipGroup(Room room)
        {
            return room != null && room.Alternated ? room.AlternateGroup : -1;
        }

        private static int Dec_GetBoxFlipGroup(dec_TombEngine_box_aux box)
        {
            if (box.FloorFlipDependencyGroup >= 0)
                return box.FloorFlipDependencyGroup;

            return Dec_GetRoomFlipGroup(box.SectorRoom);
        }

        private bool Dec_BoxExistsForCurrentStates(dec_TombEngine_box_aux box)
        {
            if (box.FloorFlipDependencyGroup >= 0)
            {
                bool dependencyFlipped = Dec_IsFlipGroupFlippedForOverlap(box.FloorFlipDependencyGroup);
                byte requiredState = dependencyFlipped ? (byte)2 : (byte)1;
                return box.FloorFlipDependencyState == requiredState;
            }

            int group = Dec_GetRoomFlipGroup(box.SectorRoom);
            if (group < 0)
                return box.RoomStateMask != 0;

            byte roomState = Dec_IsRoomFlippedForOverlap(box.SectorRoom) ?
                RoomStateFlipped : RoomStateUnflipped;
            return (box.RoomStateMask & roomState) != 0;
        }

        private bool Dec_CanShareBoxIdentity(dec_TombEngine_box_aux first, dec_TombEngine_box_aux second)
        {
            if (first.Water != second.Water || first.Shallow != second.Shallow)
                return false;

            if (first.Shallow && second.Shallow &&
                (first.Room == second.Room ||
                    Dec_BoxesShareVerticalPortal(first, second) ||
                    Dec_BoxesShareVerticalPortalStack(first, second)))
                return true;

            int firstGroup = Dec_GetRoomFlipGroup(first.Room);
            int secondGroup = Dec_GetRoomFlipGroup(second.Room);
            bool compatibleRooms = firstGroup < 0 || secondGroup < 0 || firstGroup == secondGroup ||
                first.SectorRoom == second.SectorRoom;

            return compatibleRooms &&
                first.FloorFlipDependencyGroup == second.FloorFlipDependencyGroup &&
                first.FloorFlipDependencyState == second.FloorFlipDependencyState &&
                first.FloorFlipCounterpartSignature == second.FloorFlipCounterpartSignature;
        }

        private bool Dec_IsFlipGroupFlippedForOverlap(int group)
        {
            if (group >= 0 &&
                dec_flipGroupOverrides != null &&
                dec_flipGroupOverrides.TryGetValue(group, out bool flipped))
                return flipped;

            return dec_flipped;
        }

        private bool Dec_IsRoomFlippedForOverlap(Room room)
        {
            int group = Dec_GetRoomFlipGroup(room);
            return Dec_IsFlipGroupFlippedForOverlap(group);
        }

        private bool Dec_BoxFloorMatches(int x, int z, int floor, int dependencyGroup, byte dependencyState,
            bool water, bool shallow)
        {
            dec_checkUnderwater = true;
            dec_shallowWater = false;
            if (Dec_GetHeight(x, z) != floor)
                return false;

            bool sectorWater = dec_floorRoom.Properties.Type == RoomType.Water && dec_checkUnderwater;
            return sectorWater == water && dec_shallowWater == shallow &&
                dec_floorFlipDependencyGroup == dependencyGroup &&
                dec_floorFlipDependencyState == dependencyState &&
                dec_floorFlipCounterpartSignature == dec_boxFloorCounterpartSignature &&
                dec_verticalPortalSignature == dec_boxVerticalPortalSignature;
        }

        private int Dec_FindBox(dec_TombEngine_box_aux box)
        {
            var searchBounds = Vector128.Create(box.Xmin, box.Xmax, box.Zmin, box.Zmax);
            for (int i = 0; i < dec_boxes.Count; i++)
            {
                var candidate = dec_boxes[i];
                bool sameBounds;
                if (Sse2.IsSupported)
                {
                    var candidateBounds = Vector128.Create(candidate.Xmin, candidate.Xmax, candidate.Zmin, candidate.Zmax);
                    sameBounds = Sse2.MoveMask(Sse2.CompareEqual(searchBounds, candidateBounds).AsByte()) == 0xFFFF;
                }
                else
                {
                    sameBounds =
                        candidate.Xmin == box.Xmin && candidate.Xmax == box.Xmax &&
                        candidate.Zmin == box.Zmin && candidate.Zmax == box.Zmax;
                }

                if (sameBounds &&
                    candidate.Height == box.Height &&
                    Dec_CanShareBoxIdentity(candidate, box))
                    return i;
            }

            return -1;
        }

        // Adds a box or merges matching compiled identity flags.
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private int Dec_AddBox(dec_TombEngine_box_aux box, bool mergeExisting = true)
        {
            int boxIndex = Dec_FindBox(box);

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
            else if (mergeExisting)
            {
                // Prefer the sector's own flip group when a duplicate spans a portal.
                bool sameSectorRoom = box.SectorRoom == dec_boxes[boxIndex].SectorRoom;
                bool preferSectorRoom =
                    sameSectorRoom &&
                    Dec_GetRoomFlipGroup(box.Room) >= 0 &&
                    Dec_GetRoomFlipGroup(box.Room) == Dec_GetRoomFlipGroup(box.SectorRoom);

                if (box.Water || box.Shallow || preferSectorRoom)
                    dec_boxes[boxIndex].Room = box.Room;

                // Preserve every room state in which this compiled identity was found.
                dec_boxes[boxIndex].RoomStateMask |= box.RoomStateMask;
                dec_boxes[boxIndex].Water      |= box.Water;
                dec_boxes[boxIndex].Shallow    |= box.Shallow;
                dec_boxes[boxIndex].VerticalPortalSignature |= box.VerticalPortalSignature;
            }

            if (!ReferenceEquals(dec_boxes[boxIndex], box))
            {
                foreach (Room sectorRoom in box.SectorRooms)
                    dec_boxes[boxIndex].SectorRooms.Add(sectorRoom);
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
            dec_boxSourceFlipGroup = Dec_GetRoomFlipGroup(theRoom);

            dec_room = theRoom;

            // Traverse through portals to find the actual room at this position
            Dec_ClampRoom(currentX, currentZ);

            // Reset state flags for Dec_GetHeight
            dec_splitter = false;
            dec_checkUnderwater = true;
            dec_shallowWater = false;
            dec_monkey = false;
            
            // ===================================================================================
            // GET INITIAL FLOOR HEIGHT AND PROPERTIES
            // ===================================================================================
            bool slope;
            int floor = Dec_GetHeight(currentX, currentZ, out slope);
            int floorDependencyGroup = dec_floorFlipDependencyGroup;
            byte floorDependencyState = dec_floorFlipDependencyState;
            dec_boxFloorCounterpartSignature = dec_floorFlipCounterpartSignature;
            dec_boxVerticalPortalSignature = dec_verticalPortalSignature;
            box.Height = floor;
            box.Slope = slope;
            box.FloorFlipDependencyGroup = floorDependencyGroup;
            box.FloorFlipDependencyState = floorDependencyState;
            box.FloorFlipCounterpartSignature = dec_boxFloorCounterpartSignature;
            box.VerticalPortalSignature = dec_boxVerticalPortalSignature;

            // Sector is not walkable
            if (floor == _noHeight) return false;

            // Set box room and water state
            box.Room = dec_floorRoom;
            box.SectorRoom = theRoom;
            box.SectorRooms.Add(theRoom);
            box.Water = dec_floorRoom.Properties.Type == RoomType.Water;

            // Non-alternated rooms exist in both runtime states.
            if (!theRoom.Alternated)
            {
                box.RoomStateMask = RoomStateUnflipped | RoomStateFlipped;
            }
            else if (dec_flipped)
            {
                box.RoomStateMask = RoomStateFlipped;
            }
            else
            {
                box.RoomStateMask = RoomStateUnflipped;
            }

            // Record initial monkey swing state
            if (dec_monkey)
            {
                box.Monkey = true;
                monkeyInit = true;
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
                            while (Dec_BoxFloorMatches(searchX, zMin, floor, floorDependencyGroup, floorDependencyState, box.Water, box.Shallow) &&
                                   Dec_BoxFloorMatches(searchX, zMin - 1, floor, floorDependencyGroup, floorDependencyState, box.Water, box.Shallow) &&
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

                                    if (!Dec_BoxFloorMatches(searchX, zMin - 1, floor, floorDependencyGroup, floorDependencyState, box.Water, box.Shallow)) break;

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

                            while (Dec_BoxFloorMatches(xMax, searchZ, floor, floorDependencyGroup, floorDependencyState, box.Water, box.Shallow) &&
                                   Dec_BoxFloorMatches(xMax + 1, searchZ, floor, floorDependencyGroup, floorDependencyState, box.Water, box.Shallow) &&
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

                                    if (!Dec_BoxFloorMatches(xMax + 1, searchZ, floor, floorDependencyGroup, floorDependencyState, box.Water, box.Shallow)) break;

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

                            while (Dec_BoxFloorMatches(searchX, zMax, floor, floorDependencyGroup, floorDependencyState, box.Water, box.Shallow) &&
                                   Dec_BoxFloorMatches(searchX, zMax + 1, floor, floorDependencyGroup, floorDependencyState, box.Water, box.Shallow) &&
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

                                    if (!Dec_BoxFloorMatches(searchX, zMax + 1, floor, floorDependencyGroup, floorDependencyState, box.Water, box.Shallow)) break;

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

                            while (Dec_BoxFloorMatches(xMin, searchZ, floor, floorDependencyGroup, floorDependencyState, box.Water, box.Shallow) &&
                                   Dec_BoxFloorMatches(xMin - 1, searchZ, floor, floorDependencyGroup, floorDependencyState, box.Water, box.Shallow) &&
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

                                    if (!Dec_BoxFloorMatches(xMin - 1, searchZ, floor, floorDependencyGroup, floorDependencyState, box.Water, box.Shallow)) break;

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

                // Sample the adjoining room in the current flip pass.
                Room adjoiningRoom = sector.WallPortal.AdjoiningRoom;
                adjoiningRoom = Dec_GetRoomForFlipPass(adjoiningRoom);

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
                Room adjoiningRoom = sector.FloorPortal.AdjoiningRoom;
                adjoiningRoom = Dec_GetRoomForFlipPass(adjoiningRoom);

                // Stop at water boundary (don't cross water/land transition via floor portals)
                if (room.Properties.Type == RoomType.Water != (adjoiningRoom.Properties.Type == RoomType.Water))
                    break;

                dec_overlapTraversedVerticalPortal = true;
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

        private static int Dec_GetDirectFloorSignature(Room room, int x, int z)
        {
            if (room == null)
                return int.MinValue;

            int localX = x - room.Position.X;
            int localZ = z - room.Position.Z;
            if (localX < 0 || localX >= room.NumXSectors ||
                localZ < 0 || localZ >= room.NumZSectors)
                return int.MinValue;

            Sector sector = room.Sectors[localX, localZ];
            if (sector.Type == SectorType.Wall ||
                sector.Type == SectorType.BorderWall ||
                (sector.Flags & SectorFlags.NotWalkableFloor) != 0)
                return int.MinValue;

            var floor = sector.HasGhostBlock ? sector.GhostBlock.Floor : sector.Floor;
            unchecked
            {
                int signature = 17;
                signature = signature * 31 + floor.XnZp + room.Position.Y;
                signature = signature * 31 + floor.XpZp + room.Position.Y;
                signature = signature * 31 + floor.XpZn + room.Position.Y;
                signature = signature * 31 + floor.XnZn + room.Position.Y;
                signature = signature * 31 + (int)(sector.Flags & (SectorFlags.Box | SectorFlags.Monkey));
                signature = signature * 31 + (int)room.Properties.Type;
                return signature;
            }
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
            dec_floorRoom = dec_room;
            dec_floorFlipDependencyGroup = -1;
            dec_floorFlipDependencyState = 0;
            dec_floorFlipCounterpartSignature = int.MinValue;
            dec_verticalPortalSignature = 0;

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
                adjoiningRoom = sector.WallPortal.AdjoiningRoom;
                adjoiningRoom = Dec_GetRoomForFlipPass(adjoiningRoom);
                dec_room = adjoiningRoom;
                dec_doorCheck = true;

                room = dec_room;

                localX = x - room.Position.X;
                localZ = z - room.Position.Z;

                sector = room.Sectors[localX, localZ];
            }

            var portalPos = new VectorInt2(localX, localZ);
            dec_verticalPortalSignature =
                (int)room.GetFloorRoomConnectionInfo(portalPos, true).TraversableType |
                ((int)room.GetCeilingRoomConnectionInfo(portalPos, true).TraversableType << 3);

            Room oldRoom = dec_room; 

            var connInfo = room.GetFloorRoomConnectionInfo(new VectorInt2(localX, localZ));
            while (sector.FloorPortal != null && connInfo.TraversableType != Room.RoomConnectionType.NoPortal)
            {
                adjoiningRoom = sector.FloorPortal.AdjoiningRoom;
                adjoiningRoom = Dec_GetRoomForFlipPass(adjoiningRoom);

                if (sector.FloorPortal.Opacity == PortalOpacity.SolidFaces)
                {
                    if (!((room.Properties.Type == RoomType.Water) ^ (adjoiningRoom.Properties.Type == RoomType.Water)))
                        break;
                }

                dec_overlapTraversedVerticalPortal = true;
                dec_room = adjoiningRoom;
                room = dec_room;

                localX = x - room.Position.X;
                localZ = z - room.Position.Z;

                sector = room.Sectors[localX, localZ];
                connInfo = room.GetFloorRoomConnectionInfo(new VectorInt2(localX, localZ));
            }

            dec_floorRoom = room;
            int floorGroup = Dec_GetRoomFlipGroup(room);
            if (floorGroup >= 0 && floorGroup != dec_boxSourceFlipGroup)
            {
                Room counterpartRoom = room.IsAlternate ? room.AlternateBaseRoom : room.AlternateRoom;
                int currentSignature = Dec_GetDirectFloorSignature(room, x, z);
                int counterpartSignature = Dec_GetDirectFloorSignature(counterpartRoom, x, z);
                if (currentSignature != counterpartSignature)
                {
                    dec_floorFlipDependencyGroup = floorGroup;
                    dec_floorFlipDependencyState = room.IsAlternate ? (byte)2 : (byte)1;
                    dec_floorFlipCounterpartSignature = counterpartSignature;
                }
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
            if (dec_checkUnderwater && room.Properties.Type == RoomType.Water &&
                delta <= Clicks.ToWorld(1) && sector.CeilingPortal != null)
            {
                adjoiningRoom = sector.CeilingPortal.AdjoiningRoom;
                adjoiningRoom = Dec_GetRoomForFlipPass(adjoiningRoom);

                if (adjoiningRoom.Properties.Type != RoomType.Water)
                {
                    dec_checkUnderwater = false;
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
                dec_monkey = true;
            else
                dec_monkey = false;

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
                dec_room = Dec_GetRoomForFlipPass(box.Room);

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
                dec_room = Dec_GetRoomForFlipPass(box.Room);

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
                dec_room = Dec_GetRoomForFlipPass(box.Room);

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
                dec_room = Dec_GetRoomForFlipPass(box.Room);

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
                dec_room = Dec_GetRoomForFlipPass(b.Room);

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
                dec_room = Dec_GetRoomForFlipPass(b.Room);

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
                dec_room = Dec_GetRoomForFlipPass(b.Room);

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
                dec_room = Dec_GetRoomForFlipPass(b.Room);

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
        // Sample a box room through the current pass; merged boxes may keep their base Room.
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private Room Dec_GetRoomForFlipPass(Room r)
        {
            if (r == null)
                return null;

            Room baseRoom = r.AlternateBaseRoom ?? r;
            if (Dec_IsRoomFlippedForOverlap(baseRoom) && baseRoom.AlternateRoom != null)
                return baseRoom.AlternateRoom;

            return baseRoom;
        }

        private bool Dec_BoxesShareVerticalPortal(dec_TombEngine_box_aux a, dec_TombEngine_box_aux b)
        {
            if (a.Room == b.Room)
                return false;

            int startX = Math.Max(a.Xmin, b.Xmin);
            int endX = Math.Min(a.Xmax, b.Xmax);
            int startZ = Math.Max(a.Zmin, b.Zmin);
            int endZ = Math.Min(a.Zmax, b.Zmax);
            if (startX >= endX || startZ >= endZ)
                return false;

            var lower = a.Height > b.Height ? a : b;
            var upper = lower == a ? b : a;
            Room lowerRoom = Dec_GetRoomForFlipPass(lower.Room);
            Room upperRoom = Dec_GetRoomForFlipPass(upper.Room);
            if (lowerRoom == null || upperRoom == null)
                return false;

            bool isSameFlipRoom(Room room, Room expected) =>
                room == expected || (Dec_IsRoomFlippedForOverlap(room) && room?.AlternateRoom == expected);

            bool hasPortalTo(Room room, int x, int z, bool ceiling, Room expected)
            {
                int localX = x - room.Position.X;
                int localZ = z - room.Position.Z;
                if (localX < 0 || localZ < 0 || localX >= room.NumXSectors || localZ >= room.NumZSectors)
                    return false;

                var info = ceiling ?
                    room.GetCeilingRoomConnectionInfo(new VectorInt2(localX, localZ), true) :
                    room.GetFloorRoomConnectionInfo(new VectorInt2(localX, localZ), true);

                return info.TraversableType != Room.RoomConnectionType.NoPortal &&
                    isSameFlipRoom(info.Portal?.AdjoiningRoom, expected);
            }

            for (int x = startX; x < endX; x++)
            {
                for (int z = startZ; z < endZ; z++)
                {
                    if (hasPortalTo(lowerRoom, x, z, true, upperRoom) ||
                        hasPortalTo(upperRoom, x, z, false, lowerRoom))
                        return true;
                }
            }

            return false;
        }

        private Room Dec_GetSharedActiveSectorRoom(dec_TombEngine_box_aux a, dec_TombEngine_box_aux b)
        {
            foreach (Room first in a.SectorRooms)
            {
                Room activeFirst = Dec_GetRoomForFlipPass(first);
                foreach (Room second in b.SectorRooms)
                {
                    if (activeFirst == Dec_GetRoomForFlipPass(second))
                        return activeFirst;
                }
            }

            return null;
        }

        private bool Dec_BoxesShareVerticalPortalStack(dec_TombEngine_box_aux a, dec_TombEngine_box_aux b)
        {
            Room startRoom = Dec_GetRoomForFlipPass(a.Room);
            Room targetRoom = Dec_GetRoomForFlipPass(b.Room);
            if (startRoom == null || targetRoom == null)
                return false;
            if (startRoom == targetRoom)
                return true;

            for (int x = a.Xmin; x < a.Xmax; x++)
            {
                for (int z = a.Zmin; z < a.Zmax; z++)
                {
                    var pending = new Queue<Room>();
                    var visited = new HashSet<Room> { startRoom };
                    pending.Enqueue(startRoom);

                    while (pending.Count != 0)
                    {
                        Room room = pending.Dequeue();
                        int localX = x - room.Position.X;
                        int localZ = z - room.Position.Z;
                        if (localX < 0 || localZ < 0 ||
                            localX >= room.NumXSectors || localZ >= room.NumZSectors)
                            continue;

                        var position = new VectorInt2(localX, localZ);
                        var floor = room.GetFloorRoomConnectionInfo(position, true);
                        var ceiling = room.GetCeilingRoomConnectionInfo(position, true);

                        foreach (var connection in new[] { floor, ceiling })
                        {
                            if (connection.TraversableType == Room.RoomConnectionType.NoPortal)
                                continue;

                            Room next = Dec_GetRoomForFlipPass(connection.Portal?.AdjoiningRoom);
                            if (next == targetRoom)
                                return true;

                            if (next != null && visited.Add(next))
                                pending.Enqueue(next);
                        }
                    }
                }
            }

            return false;
        }

        private bool Dec_BoxEdgeHasVerticalPortal(dec_TombEngine_box_aux box, dec_TombEngine_box_aux other)
        {
            bool HasPortal(int x, int z) =>
                Dec_BoxOwnsActiveSector(box, x, z) && dec_verticalPortalSignature != 0;

            if (box.Xmin == other.Xmax || box.Xmax == other.Xmin)
            {
                int x = box.Xmin == other.Xmax ? box.Xmin : box.Xmax - 1;
                for (int z = Math.Max(box.Zmin, other.Zmin); z < Math.Min(box.Zmax, other.Zmax); z++)
                {
                    if (HasPortal(x, z))
                        return true;
                }
            }

            if (box.Zmin == other.Zmax || box.Zmax == other.Zmin)
            {
                int z = box.Zmin == other.Zmax ? box.Zmin : box.Zmax - 1;
                for (int x = Math.Max(box.Xmin, other.Xmin); x < Math.Min(box.Xmax, other.Xmax); x++)
                {
                    if (HasPortal(x, z))
                        return true;
                }
            }

            return false;
        }

        private bool Dec_NeedsGroundRouteExitFloorHint(dec_TombEngine_box_aux from, dec_TombEngine_box_aux to)
        {
            int heightDiff = Math.Abs(Dec_GetOverlapHeightDelta(from, to));
            // Only ground route-exit floor retries consume this hint.
            if (heightDiff > Clicks.ToWorld(4))
                return false;

            return dec_overlapTraversedVerticalPortal ||
                Dec_BoxEdgeHasVerticalPortal(to, from) ||
                Dec_BoxesShareVerticalPortal(from, to);
        }

        private bool Dec_GetFloorCorners(dec_TombEngine_box_aux box, int x, int z, out int[] corners)
        {
            corners = null;
            dec_room = Dec_GetRoomForFlipPass(box.Room);
            int height = Dec_GetHeight(x, z);
            if (height == _noHeight)
                return false;

            int average = (dec_cornerHeight1 + dec_cornerHeight2 +
                dec_cornerHeight3 + dec_cornerHeight4) / 4;
            int roomY = height - average;
            corners = new[]
            {
                dec_cornerHeight1 + roomY,
                dec_cornerHeight2 + roomY,
                dec_cornerHeight3 + roomY,
                dec_cornerHeight4 + roomY
            };
            return true;
        }

        private bool Dec_SharedXEdgeHeightsMatch(dec_TombEngine_box_aux first,
            dec_TombEngine_box_aux second, int firstX, int secondX,
            int firstCornerA, int firstCornerB, int secondCornerA, int secondCornerB)
        {
            for (int z = Math.Max(first.Zmin, second.Zmin); z < Math.Min(first.Zmax, second.Zmax); z++)
            {
                if (!Dec_GetFloorCorners(first, firstX, z, out int[] firstCorners) ||
                    !Dec_GetFloorCorners(second, secondX, z, out int[] secondCorners) ||
                    firstCorners[firstCornerA] != secondCorners[secondCornerA] ||
                    firstCorners[firstCornerB] != secondCorners[secondCornerB])
                    return false;
            }
            return true;
        }

        private bool Dec_SharedZEdgeHeightsMatch(dec_TombEngine_box_aux first,
            dec_TombEngine_box_aux second, int firstZ, int secondZ,
            int firstCornerA, int firstCornerB, int secondCornerA, int secondCornerB)
        {
            for (int x = Math.Max(first.Xmin, second.Xmin); x < Math.Min(first.Xmax, second.Xmax); x++)
            {
                if (!Dec_GetFloorCorners(first, x, firstZ, out int[] firstCorners) ||
                    !Dec_GetFloorCorners(second, x, secondZ, out int[] secondCorners) ||
                    firstCorners[firstCornerA] != secondCorners[secondCornerA] ||
                    firstCorners[firstCornerB] != secondCorners[secondCornerB])
                    return false;
            }
            return true;
        }

        private int Dec_GetOverlapHeightDelta(dec_TombEngine_box_aux from, dec_TombEngine_box_aux to)
        {
            int minDelta = int.MaxValue;
            int maxDelta = int.MinValue;

            void AddDelta(int sourceHeight, int targetHeight)
            {
                int delta = sourceHeight - targetHeight;
                minDelta = Math.Min(minDelta, delta);
                maxDelta = Math.Max(maxDelta, delta);
            }

            if (from.Xmax == to.Xmin || from.Xmin == to.Xmax)
            {
                bool positive = from.Xmax == to.Xmin;
                int sourceX = positive ? from.Xmax - 1 : from.Xmin;
                int targetX = positive ? to.Xmin : to.Xmax - 1;
                int sourceA = positive ? 1 : 0;
                int sourceB = positive ? 2 : 3;
                int targetA = positive ? 0 : 1;
                int targetB = positive ? 3 : 2;

                for (int z = Math.Max(from.Zmin, to.Zmin); z < Math.Min(from.Zmax, to.Zmax); z++)
                {
                    if (!Dec_GetFloorCorners(from, sourceX, z, out int[] sourceCorners) ||
                        !Dec_GetFloorCorners(to, targetX, z, out int[] targetCorners))
                        return from.Height - to.Height;

                    AddDelta(sourceCorners[sourceA], targetCorners[targetA]);
                    AddDelta(sourceCorners[sourceB], targetCorners[targetB]);
                }
            }
            else if (from.Zmax == to.Zmin || from.Zmin == to.Zmax)
            {
                bool positive = from.Zmax == to.Zmin;
                int sourceZ = positive ? from.Zmax - 1 : from.Zmin;
                int targetZ = positive ? to.Zmin : to.Zmax - 1;
                int sourceA = positive ? 0 : 3;
                int sourceB = positive ? 1 : 2;
                int targetA = positive ? 3 : 0;
                int targetB = positive ? 2 : 1;

                for (int x = Math.Max(from.Xmin, to.Xmin); x < Math.Min(from.Xmax, to.Xmax); x++)
                {
                    if (!Dec_GetFloorCorners(from, x, sourceZ, out int[] sourceCorners) ||
                        !Dec_GetFloorCorners(to, x, targetZ, out int[] targetCorners))
                        return from.Height - to.Height;

                    AddDelta(sourceCorners[sourceA], targetCorners[targetA]);
                    AddDelta(sourceCorners[sourceB], targetCorners[targetB]);
                }
            }

            if (minDelta == int.MaxValue)
                return from.Height - to.Height;

            // Represent a sloped shared edge by its center, not its easiest endpoint.
            return (int)(((long)minDelta + maxDelta) / 2);
        }

        private bool Dec_IsLevelShallowWaterEdge(dec_TombEngine_box_aux shallow,
            dec_TombEngine_box_aux water)
        {
            int xMin = Math.Max(shallow.Xmin, water.Xmin);
            int xMax = Math.Min(shallow.Xmax, water.Xmax);
            int zMin = Math.Max(shallow.Zmin, water.Zmin);
            int zMax = Math.Min(shallow.Zmax, water.Zmax);
            int cornerA;
            int cornerB;

            if (shallow.Xmax == water.Xmin)
            {
                xMin = shallow.Xmax - 1;
                xMax = shallow.Xmax;
                cornerA = 1;
                cornerB = 2;
            }
            else if (shallow.Xmin == water.Xmax)
            {
                xMin = shallow.Xmin;
                xMax = shallow.Xmin + 1;
                cornerA = 0;
                cornerB = 3;
            }
            else if (shallow.Zmax == water.Zmin)
            {
                zMin = shallow.Zmax - 1;
                zMax = shallow.Zmax;
                cornerA = 0;
                cornerB = 1;
            }
            else if (shallow.Zmin == water.Zmax)
            {
                zMin = shallow.Zmin;
                zMax = shallow.Zmin + 1;
                cornerA = 3;
                cornerB = 2;
            }
            else
                return false;

            for (int x = xMin; x < xMax; x++)
            for (int z = zMin; z < zMax; z++)
            {
                if (!Dec_GetFloorCorners(shallow, x, z, out int[] corners))
                    return false;

                if (corners[cornerA] != corners[cornerB])
                    return false;
            }

            return xMin < xMax && zMin < zMax;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool Dec_TestOverlapXmax(dec_TombEngine_box_aux test, dec_TombEngine_box_aux box,
            Room sharedSectorRoom = null)
        {
            int startZ = test.Zmin > box.Zmin ? test.Zmin : box.Zmin;
            int endZ = test.Zmax < box.Zmax ? test.Zmax : box.Zmax;

            for (int z = startZ; z < endZ; z++)
            {
                dec_room = sharedSectorRoom ?? Dec_GetRoomForFlipPass(test.Room);
                if (!Dec_ClampRoom(test.Xmax - 1, z))
                    return false;

                // Reject phantom overlaps between edge-touching rooms with no real portal.
                if (sharedSectorRoom == null && test.Room != box.Room)
                {
                    dec_room = Dec_GetRoomForFlipPass(test.Room);
                    if (!Dec_ClampRoom(test.Xmax, z) ||
                        dec_room != Dec_GetRoomForFlipPass(box.Room))
                        return false;
                }

                // Sample the box-side floor from the box's own room for this pass.
                dec_room = sharedSectorRoom ?? Dec_GetRoomForFlipPass(box.Room);
                if (!Dec_ClampRoom(test.Xmax, z))
                    return false;

                dec_splitter = false;

                int floorHeight = Dec_GetHeight(test.Xmax, z);
                if (floorHeight == _noHeight ||
                    (sharedSectorRoom == null && box.Height != floorHeight))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Tests edge adjacency when test.Xmin touches box (left edge overlap).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool Dec_TestOverlapXmin(dec_TombEngine_box_aux test, dec_TombEngine_box_aux box,
            Room sharedSectorRoom = null)
        {
            int startZ = test.Zmin > box.Zmin ? test.Zmin : box.Zmin;
            int endZ = test.Zmax < box.Zmax ? test.Zmax : box.Zmax;

            for (int z = startZ; z < endZ; z++)
            {
                dec_room = sharedSectorRoom ?? Dec_GetRoomForFlipPass(test.Room);
                if (!Dec_ClampRoom(test.Xmin, z))
                    return false;

                // Reject phantom overlaps between edge-touching rooms with no real portal.
                if (sharedSectorRoom == null && test.Room != box.Room)
                {
                    dec_room = Dec_GetRoomForFlipPass(test.Room);
                    if (!Dec_ClampRoom(test.Xmin - 1, z) ||
                        dec_room != Dec_GetRoomForFlipPass(box.Room))
                        return false;
                }

                // Sample the box-side floor from the box's own room for this pass.
                dec_room = sharedSectorRoom ?? Dec_GetRoomForFlipPass(box.Room);
                if (!Dec_ClampRoom(test.Xmin - 1, z))
                    return false;

                dec_splitter = false;

                int floorHeight = Dec_GetHeight(test.Xmin - 1, z);
                if (floorHeight == _noHeight ||
                    (sharedSectorRoom == null && box.Height != floorHeight))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Tests edge adjacency when test.Zmax touches box (top edge overlap).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool Dec_TestOverlapZmax(dec_TombEngine_box_aux test, dec_TombEngine_box_aux box,
            Room sharedSectorRoom = null)
        {
            int startX = test.Xmin > box.Xmin ? test.Xmin : box.Xmin;
            int endX = test.Xmax < box.Xmax ? test.Xmax : box.Xmax;

            for (int x = startX; x < endX; x++)
            {
                dec_room = sharedSectorRoom ?? Dec_GetRoomForFlipPass(test.Room);
                if (!Dec_ClampRoom(x, test.Zmax - 1))
                    return false;

                // Reject phantom overlaps between edge-touching rooms with no real portal.
                if (sharedSectorRoom == null && test.Room != box.Room)
                {
                    dec_room = Dec_GetRoomForFlipPass(test.Room);
                    if (!Dec_ClampRoom(x, test.Zmax) ||
                        dec_room != Dec_GetRoomForFlipPass(box.Room))
                        return false;
                }

                // Sample the box-side floor from the box's own room for this pass.
                dec_room = sharedSectorRoom ?? Dec_GetRoomForFlipPass(box.Room);
                if (!Dec_ClampRoom(x, test.Zmax))
                    return false;

                dec_splitter = false;

                int floorHeight = Dec_GetHeight(x, test.Zmax);
                if (floorHeight == _noHeight ||
                    (sharedSectorRoom == null && box.Height != floorHeight))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Tests edge adjacency when test.Zmin touches box (bottom edge overlap).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool Dec_TestOverlapZmin(dec_TombEngine_box_aux test, dec_TombEngine_box_aux box,
            Room sharedSectorRoom = null)
        {
            int startX = test.Xmin > box.Xmin ? test.Xmin : box.Xmin;
            int endX = test.Xmax < box.Xmax ? test.Xmax : box.Xmax;

            for (int x = startX; x < endX; x++)
            {
                dec_room = sharedSectorRoom ?? Dec_GetRoomForFlipPass(test.Room);
                if (!Dec_ClampRoom(x, test.Zmin))
                    return false;

                // Reject phantom overlaps between edge-touching rooms with no real portal.
                if (sharedSectorRoom == null && test.Room != box.Room)
                {
                    dec_room = Dec_GetRoomForFlipPass(test.Room);
                    if (!Dec_ClampRoom(x, test.Zmin - 1) ||
                        dec_room != Dec_GetRoomForFlipPass(box.Room))
                        return false;
                }

                // Sample the box-side floor from the box's own room for this pass.
                dec_room = sharedSectorRoom ?? Dec_GetRoomForFlipPass(box.Room);
                if (!Dec_ClampRoom(x, test.Zmin - 1))
                    return false;

                dec_splitter = false;

                int floorHeight = Dec_GetHeight(x, test.Zmin - 1);
                if (floorHeight == _noHeight ||
                    (sharedSectorRoom == null && box.Height != floorHeight))
                    return false;
            }

            return true;
        }

        private bool Dec_BoxOwnsActiveSector(dec_TombEngine_box_aux box, int x, int z)
        {
            foreach (Room sectorRoom in box.SectorRooms)
            {
                Room activeRoom = Dec_GetRoomForFlipPass(sectorRoom);
                if (activeRoom == null)
                    continue;

                int localX = x - activeRoom.Position.X;
                int localZ = z - activeRoom.Position.Z;
                if (localX < 0 || localZ < 0 ||
                    localX >= activeRoom.NumXSectors || localZ >= activeRoom.NumZSectors)
                    continue;

                dec_room = activeRoom;
                dec_boxFloorCounterpartSignature = box.FloorFlipCounterpartSignature;
                dec_boxVerticalPortalSignature = box.VerticalPortalSignature;
                if (Dec_BoxFloorMatches(x, z, box.Height,
                    box.FloorFlipDependencyGroup, box.FloorFlipDependencyState,
                    box.Water, box.Shallow))
                    return true;
            }

            return false;
        }

        private bool Dec_BoxesShareActiveEdge(dec_TombEngine_box_aux a, dec_TombEngine_box_aux b)
        {
            if (a.Xmax == b.Xmin || a.Xmin == b.Xmax)
            {
                int aX = a.Xmax == b.Xmin ? a.Xmax - 1 : a.Xmin;
                int bX = a.Xmax == b.Xmin ? b.Xmin : b.Xmax - 1;
                for (int z = Math.Max(a.Zmin, b.Zmin); z < Math.Min(a.Zmax, b.Zmax); z++)
                {
                    if (Dec_BoxOwnsActiveSector(a, aX, z) && Dec_BoxOwnsActiveSector(b, bX, z))
                        return true;
                }
            }

            if (a.Zmax == b.Zmin || a.Zmin == b.Zmax)
            {
                int aZ = a.Zmax == b.Zmin ? a.Zmax - 1 : a.Zmin;
                int bZ = a.Zmax == b.Zmin ? b.Zmin : b.Zmax - 1;
                for (int x = Math.Max(a.Xmin, b.Xmin); x < Math.Min(a.Xmax, b.Xmax); x++)
                {
                    if (Dec_BoxOwnsActiveSector(a, x, aZ) && Dec_BoxOwnsActiveSector(b, x, bZ))
                        return true;
                }
            }

            return false;
        }

        private bool Dec_CheckAnyOverlap(dec_TombEngine_box_aux a, dec_TombEngine_box_aux b)
        {
            dec_flyerOnly = false;
            if (Dec_CheckOverlap(a, b))
                return true;

            bool differentEnvironment = a.Water != b.Water;
            bool sharesXEdge = (a.Xmax == b.Xmin || a.Xmin == b.Xmax) &&
                a.Zmax > b.Zmin && a.Zmin < b.Zmax;
            bool sharesZEdge = (a.Zmax == b.Zmin || a.Zmin == b.Zmax) &&
                a.Xmax > b.Xmin && a.Xmin < b.Xmax;
            bool sharesAirSpace = a.SectorRooms.Overlaps(b.SectorRooms) ||
                Dec_BoxesShareActiveEdge(a, b);
            if (!differentEnvironment || (!sharesXEdge && !sharesZEdge) || !sharesAirSpace)
                return false;

            dec_jump = false;
            dec_monkey = false;
            dec_routeExitFloorHint = false;
            dec_flyerOnly = true;
            return true;
        }

        /// <summary>
        /// Main overlap check between two boxes.
        ///
        /// OVERLAP TYPES:
        /// ==============
        /// 1. STRAIGHT OVERLAP: Boxes overlap in both X and Z, optionally through a vertical portal
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
            dec_routeExitFloorHint = false;
            dec_overlapTraversedVerticalPortal = false;

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
            bool isShallowWaterEdge =
                ((box1.Shallow && box2.Water && !box2.Shallow) ||
                 (box2.Shallow && box1.Water && !box1.Shallow));
            Room shallowWaterRoom = isShallowWaterEdge ?
                Dec_GetSharedActiveSectorRoom(box1, box2) : null;

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
                    bool edgeMatches = Dec_SharedXEdgeHeightsMatch(box1, box2,
                        box1.Xmax - 1, box1.Xmax, 1, 2, 0, 3);
                    if (!Dec_TestOverlapXmax(box1, box2, shallowWaterRoom) && !edgeMatches &&
                        (box1.Height != box2.Height || !Dec_TestOverlapXmin(box2, box1, shallowWaterRoom)))
                        return false;
                }
                else if (box1.Xmin == box2.Xmax)
                {
                    bool edgeMatches = Dec_SharedXEdgeHeightsMatch(box1, box2,
                        box1.Xmin, box1.Xmin - 1, 0, 3, 1, 2);
                    if (!Dec_TestOverlapXmin(box1, box2, shallowWaterRoom) && !edgeMatches &&
                        (box1.Height != box2.Height || !Dec_TestOverlapXmax(box2, box1, shallowWaterRoom)))
                        return false;
                }
                else
                {
                    return false;
                }

                if (isShallowWaterEdge &&
                    !Dec_IsLevelShallowWaterEdge(box1.Shallow ? box1 : box2, box1.Shallow ? box2 : box1))
                    return false;

                if (box1.Monkey && box2.Monkey) dec_monkey = true;
                dec_routeExitFloorHint = Dec_NeedsGroundRouteExitFloorHint(a, b);
                return true;
            }

            // ===================================================================================
            // CASE 2: X overlap and Z overlap - straight overlap
            // ===================================================================================
            if (hasZOverlap)
            {
                // Overlapping floors from different stacked rooms connect only through
                // a real vertical portal, even when their heights happen to match.
                if (Dec_GetRoomForFlipPass(a.Room) != Dec_GetRoomForFlipPass(b.Room) &&
                    !Dec_BoxesShareVerticalPortal(a, b))
                    return false;

                bool aContainsB =
                    a.Xmin <= b.Xmin && a.Xmax >= b.Xmax &&
                    a.Zmin <= b.Zmin && a.Zmax >= b.Zmax;
                bool bContainsA =
                    b.Xmin <= a.Xmin && b.Xmax >= a.Xmax &&
                    b.Zmin <= a.Zmin && b.Zmax >= a.Zmax;
                bool sameBounds =
                    a.Xmin == b.Xmin && a.Xmax == b.Xmax &&
                    a.Zmin == b.Zmin && a.Zmax == b.Zmax;

                // A contained coplanar box is another identity of the same surface,
                // not a path transition.
                if (Dec_GetRoomForFlipPass(a.Room) == Dec_GetRoomForFlipPass(b.Room) &&
                    box1.Height == box2.Height && !sameBounds && (aContainsB || bContainsA))
                    return false;

                if (box1.Height != box2.Height)
                {
                    dec_routeExitFloorHint = Dec_NeedsGroundRouteExitFloorHint(a, b);
                    return true;
                }

                if (box1.Monkey && box2.Monkey) dec_monkey = true;
                dec_routeExitFloorHint = Dec_NeedsGroundRouteExitFloorHint(a, b);
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
                bool edgeMatches = Dec_SharedZEdgeHeightsMatch(box1, box2,
                    box1.Zmax - 1, box1.Zmax, 0, 1, 3, 2);
                if (!Dec_TestOverlapZmax(box1, box2, shallowWaterRoom) && !edgeMatches &&
                    (box1.Height != box2.Height || !Dec_TestOverlapZmin(box2, box1, shallowWaterRoom)))
                    return false;
            }
            else if (box1.Zmin == box2.Zmax)
            {
                bool edgeMatches = Dec_SharedZEdgeHeightsMatch(box1, box2,
                    box1.Zmin, box1.Zmin - 1, 3, 2, 0, 1);
                if (!Dec_TestOverlapZmin(box1, box2, shallowWaterRoom) && !edgeMatches &&
                    (box1.Height != box2.Height || !Dec_TestOverlapZmax(box2, box1, shallowWaterRoom)))
                    return false;
            }
            else
            {
                return false;
            }

            if (isShallowWaterEdge &&
                !Dec_IsLevelShallowWaterEdge(box1.Shallow ? box1 : box2, box1.Shallow ? box2 : box1))
                return false;

            if (box1.Monkey && box2.Monkey) dec_monkey = true;
            dec_routeExitFloorHint = Dec_NeedsGroundRouteExitFloorHint(a, b);
            return true;
        }
    }
}
