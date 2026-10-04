namespace ProjectSpy.Core.Missions;

/// <summary>
/// One region of a mission map: a building, or the forward command post beside it.
/// </summary>
/// <remarks>
/// A region exists so that the forward command post can be a genuinely separate piece
/// of map rather than a room bolted onto a floor. The post has its own floors, its own
/// rooms and its own connections, and the only thing that joins it to the building is
/// one <see cref="SiteConnection"/> whose step cost is
/// <c>site_gen_rule.site_forward_post_travel_steps</c>. Keeping it a separate region is
/// what makes that honest — a post modelled as a room on a high floor would be reachable
/// through the building's own doors, and the journey would cost nothing.
///
/// The post's floors are numbered past the building's. Both regions draw from floor
/// index zero and a <see cref="TacticalPosition"/> carries nothing else that could say
/// which one it is in, so overlapping indices would make "which room is that?" unanswerable
/// for anyone standing in the post.
/// </remarks>
public sealed class SiteRegion
{
    /// <summary>Which part of the mission this region is.</summary>
    public SiteRegionKind Kind { get; init; }

    /// <summary>Floors, bottom to top.</summary>
    public List<SiteFloor> Floors { get; } = new();

    /// <summary>Connections whose two rooms are both in this region.</summary>
    public List<SiteConnection> Connections { get; } = new();

    /// <summary>Light emitters, across all this region's floors.</summary>
    public List<SiteLight> Lights { get; } = new();

    /// <summary>Sight blockers, across all this region's floors.</summary>
    public List<SiteOccluder> Occluders { get; } = new();

    /// <summary>Placed interactables, across all this region's floors.</summary>
    public List<SiteInteractable> Interactables { get; } = new();

    /// <summary>Guards stationed here.</summary>
    public List<SiteGuard> Guards { get; } = new();

    /// <summary>Civilians standing here.</summary>
    public List<SiteCivilian> Civilians { get; } = new();

    /// <summary>Every room in this region, floor by floor, left to right.</summary>
    public IEnumerable<SiteRoom> AllRooms
    {
        get
        {
            foreach (SiteFloor floor in Floors)
            {
                foreach (SiteRoom room in floor.Rooms)
                    yield return room;
            }
        }
    }

    /// <summary>Total room count across every floor.</summary>
    public int RoomCount
    {
        get
        {
            int total = 0;
            foreach (SiteFloor floor in Floors)
                total += floor.Rooms.Count;
            return total;
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Kind} floors={Floors.Count} rooms={RoomCount}";
}