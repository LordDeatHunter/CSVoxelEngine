namespace VoxelEngine.Server;

internal readonly record struct Inbound(ClientConnection Client, byte[]? Payload);
