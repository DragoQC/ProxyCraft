namespace ProxyCraft.Services;

public sealed class ProxyConflictException(string message) : Exception(message);
