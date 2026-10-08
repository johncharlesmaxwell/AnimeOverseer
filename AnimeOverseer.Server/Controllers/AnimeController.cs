using Microsoft.AspNetCore.Mvc;
using AnimeOverseer.Server.Models.Api;
using AnimeOverseer.Server.Services;

namespace AnimeOverseer.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AnimeController : ControllerBase
{
    private readonly IAnimeDataSource _dataSource;

    public AnimeController(IAnimeDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    [HttpGet("season")]
    public async Task<IActionResult> GetSeasonAnimes(
        [FromQuery] int year,
        [FromQuery] string season = "spring")
    {
        var animes = await _dataSource.GetSeasonAnimes(year, season);
        return Ok(animes.Select(anime => anime.ToCatalogDto()).ToList());
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return BadRequest("Query parameter is required.");

        var animes = await _dataSource.SearchAsync(query);
        return Ok(animes.Select(anime => anime.ToCatalogDto()).ToList());
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var anime = await _dataSource.GetByIdAsync(id);
        if (anime == null)
            return NotFound();

        return Ok(anime.ToCatalogDto());
    }

    [HttpGet("genres")]
    public async Task<IActionResult> GetGenres()
    {
        var genres = await _dataSource.GetAllGenresAsync();
        return Ok(genres);
    }

    [HttpGet("themes")]
    public async Task<IActionResult> GetThemes()
    {
        var themes = await _dataSource.GetAllThemesAsync();
        return Ok(themes);
    }

    [HttpGet("demographics")]
    public async Task<IActionResult> GetDemographics()
    {
        var demographics = await _dataSource.GetAllDemographicsAsync();
        return Ok(demographics);
    }
}
