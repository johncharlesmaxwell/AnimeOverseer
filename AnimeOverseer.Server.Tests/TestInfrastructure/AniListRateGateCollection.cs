using Xunit;

namespace AnimeOverseer.Server.Tests.TestInfrastructure;

/// <summary>
/// AniListApiService spaces requests through a process-wide gate. Test classes
/// that send AniList requests share this collection so they never run in
/// parallel and push each other's requests past short background-job deadlines.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AniListRateGateCollection
{
    public const string Name = "AniList rate gate";
}
