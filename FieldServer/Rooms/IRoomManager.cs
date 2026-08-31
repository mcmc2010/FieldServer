namespace FieldServer.Rooms;

/// <summary>房间管理服务。</summary>
public interface IRoomManager
{
    int RoomCount { get; }
    IReadOnlyList<IRoom> Rooms { get; }
    IRoom? GetRoom(int id);
}
