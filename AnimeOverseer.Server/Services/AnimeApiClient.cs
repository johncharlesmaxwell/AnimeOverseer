using System.Net.Http.Json;
using AnimeOverseer.Server.Models.Api;

namespace AnimeOverseer.Server.Services;

public class AnimeApiClient(HttpClient httpClient)
{
    private const string BaseApiPath = "api/anime";

    public async Task<List<AnimeCatalogDto>> GetSeasonAnimesAsync(int year, string season)
    {
        var result = await httpClient.GetFromJsonAsync<List<AnimeCatalogDto>>($"{BaseApiPath}/season?year={year}&season={Uri.EscapeDataString(season)}");
        return result ?? [];
    }

    public async Task<List<AnimeCatalogDto>> SearchAnimeAsync(string query)
    {
        var result = await httpClient.GetFromJsonAsync<List<AnimeCatalogDto>>($"{BaseApiPath}/search?query={Uri.EscapeDataString(query)}");
        return result ?? [];
    }

    public async Task<AnimeCatalogDto?> GetAnimeByIdAsync(int id)
    {
        var response = await httpClient.GetAsync($"{BaseApiPath}/{id}");
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<AnimeCatalogDto>();
    }

    public async Task<List<Models.Genre>> GetAllGenresAsync()
    {
        var result = await httpClient.GetFromJsonAsync<List<Models.Genre>>($"{BaseApiPath}/genres");
        return result ?? [];
    }
}
