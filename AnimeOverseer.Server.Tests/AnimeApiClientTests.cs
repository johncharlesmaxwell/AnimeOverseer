using System.Net;
using System.Text.Json;
using AnimeOverseer.Server.Models.Api;
using AnimeOverseer.Server.Services;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public sealed class AnimeApiClientTests
{
    [Fact]
    public async Task Client_reads_catalogue_dtos_and_uses_controller_search_parameter()
    {
        var expected = new AnimeCatalogDto
        {
            Id = 42,
            Title = "Spy x Family",
            AlternativeTitles = ["Spy Family"],
            Genres = [new AnimeTagDto(7, "Comedy")]
        };
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var handler = new JsonHandler(request =>
        {
            var payload = request.RequestUri!.AbsolutePath.EndsWith("/42", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(expected, jsonOptions)
                : JsonSerializer.Serialize(new[] { expected }, jsonOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload)
            };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var client = new AnimeApiClient(httpClient);

        var season = await client.GetSeasonAnimesAsync(2026, "fall");
        var search = await client.SearchAnimeAsync("Spy x Family");
        var detail = await client.GetAnimeByIdAsync(42);

        Assert.Equal("Spy Family", Assert.Single(season).AlternativeTitles.Single());
        Assert.Equal("Comedy", Assert.Single(search).Genres.Single().Name);
        Assert.Equal("Spy x Family", detail?.Title);
        Assert.Contains("/api/anime/search?query=Spy%20x%20Family", handler.Requests[1]);
        Assert.DoesNotContain("/api/anime/search?q=", handler.Requests[1]);
        Assert.Contains("/api/anime/season?year=2026&season=fall", handler.Requests[0]);
        Assert.Contains("/api/anime/42", handler.Requests[2]);
    }

    private sealed class JsonHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request));
        }
    }
}
