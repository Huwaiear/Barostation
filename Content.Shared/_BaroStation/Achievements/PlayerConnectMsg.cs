using Robust.Shared.Serialization;

namespace Content.Shared._BaroStation.Achievements;

[Serializable, NetSerializable]
public sealed class PlayerConnectMsg : EntityEventArgs
{
    // Пустое сообщение, просто сигнал о подключении
}
