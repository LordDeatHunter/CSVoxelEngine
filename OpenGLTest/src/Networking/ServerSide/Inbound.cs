namespace VoxelEngine.Networking.ServerSide;

internal readonly record struct Inbound(ClientConnection Client, byte[]? Payload);
