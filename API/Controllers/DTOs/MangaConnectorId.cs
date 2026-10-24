using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using API.Schema.MangaContext;

namespace API.Controllers.DTOs;

/// <summary>
/// <see cref="MangaConnectorId{T}"/> DTO
/// </summary>
public sealed record MangaConnectorId<T>(string Key, string MangaConnectorName, string ForeignKey, string? WebsiteUrl, bool UseForDownload) : Identifiable(Key) where T : class
{
    /// <summary>
    /// Name of the Connector
    /// </summary>
    [Required]
    [Description("Name of the Connector")]
    public string MangaConnectorName { get; init; } = MangaConnectorName;
    
    /// <summary>
    /// Key of the referenced DTO
    /// </summary>
    [Required]
    [Description("Key of the referenced DTO")]
    public string ForeignKey { get; init; } = ForeignKey;
    
    /// <summary>
    /// Website Link for reference, if any
    /// </summary>
    [Description("Website Link for reference, if any")]
    public string? WebsiteUrl { get; init; } = WebsiteUrl;
    
    /// <summary>
    /// Whether this Link is used for downloads
    /// </summary>
    [Required]
    [Description("Whether this Link is used for downloads")]
    public bool UseForDownload { get; init; } = UseForDownload;

    [Description("Language of this item on the Connector (chapters only), e.g. \"en\"")]
    public string? Language { get; init; }

    [Required]
    [Description("Languages the Connector provides (e.g. [\"it\"] for Mangaworld)")]
    public string[] Languages { get; init; } = Tranga.TryGetMangaConnector(MangaConnectorName, out API.MangaConnectors.MangaConnector? connector)
        ? connector.SupportedLanguages
        : [];
}