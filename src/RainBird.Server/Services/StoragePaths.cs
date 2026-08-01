namespace RainBird.Server.Services;

/// <summary>
/// Where the things that must outlive a container live.
///
/// Both default to folders beside the application, and both can be pointed anywhere
/// — which is the whole point in a container, where the alternative is a Docker
/// volume you have to go excavating for when you want a backup.
///
/// Resolved once at startup and injected, rather than each caller recomputing a path
/// from the content root. Two places used to do that, and they would have quietly
/// disagreed the moment one of them became configurable.
/// </summary>
/// <param name="Data">The SQLite database, Data Protection keys, and the token signing key.</param>
/// <param name="Media">Zone photos.</param>
public sealed record StoragePaths(string Data, string Media);
