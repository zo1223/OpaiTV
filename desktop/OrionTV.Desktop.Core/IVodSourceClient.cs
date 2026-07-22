namespace OrionTV.Desktop.Core;

/// <summary>点播源客户端统一接口（按 VideoSource.Protocol 路由）。</summary>
public interface IVodSourceClient
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(VideoSource source, string query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchResult>> GetByIdAsync(VideoSource source, string id, CancellationToken cancellationToken = default);

    Task<VideoDetail> GetDetailAsync(VideoSource source, string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VideoSourceCategory>> GetCategoriesAsync(VideoSource source, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchResult>> GetSourcePageAsync(VideoSource source, int pageStart, int pageSize, CancellationToken cancellationToken = default);

    Task<CategoryVideosResponse> GetCategoryVideosAsync(VideoSource source, string categoryId, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchResult>> GetHomePageAsync(VideoSource source, string type, string tag, int pageStart, int pageSize, CancellationToken cancellationToken = default);
}

/// <summary>按协议选择 <see cref="IVodSourceClient"/>。</summary>
public sealed class VodSourceClientFactory
{
    private readonly AppleCmsClient _appleCms;
    private readonly MacCmsJsonClient _macCmsJson;
    private readonly MacCmsXmlClient _macCmsXml;
    private readonly UnsupportedVodSourceClient _unsupported = new();

    public VodSourceClientFactory(HttpClient httpClient)
    {
        _appleCms = new AppleCmsClient(httpClient);
        _macCmsJson = new MacCmsJsonClient(httpClient);
        _macCmsXml = new MacCmsXmlClient(httpClient);
    }

    public VodSourceClientFactory(AppleCmsClient appleCms, MacCmsJsonClient macCmsJson, MacCmsXmlClient macCmsXml)
    {
        _appleCms = appleCms;
        _macCmsJson = macCmsJson;
        _macCmsXml = macCmsXml;
    }

    /// <summary>默认 AppleCMS JSON 客户端（兼容旧代码直接调用）。</summary>
    public AppleCmsClient AppleCms => _appleCms;

    public IVodSourceClient For(VideoSource source) =>
        VideoSourceProtocols.Normalize(source.Protocol) switch
        {
            VideoSourceProtocols.AppleCms => _appleCms,
            VideoSourceProtocols.MacCmsJson => _macCmsJson,
            VideoSourceProtocols.MacCmsXml => _macCmsXml,
            _ => _unsupported,
        };
}

/// <summary>未知协议：返回空结果，避免拖垮聚合搜索。</summary>
internal sealed class UnsupportedVodSourceClient : IVodSourceClient
{
    public Task<IReadOnlyList<SearchResult>> SearchAsync(VideoSource source, string query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SearchResult>>([]);

    public Task<IReadOnlyList<SearchResult>> GetByIdAsync(VideoSource source, string id, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SearchResult>>([]);

    public Task<VideoDetail> GetDetailAsync(VideoSource source, string id, CancellationToken cancellationToken = default) =>
        Task.FromException<VideoDetail>(new InvalidOperationException($"UNSUPPORTED_VOD_PROTOCOL:{source.Protocol}"));

    public Task<IReadOnlyList<VideoSourceCategory>> GetCategoriesAsync(VideoSource source, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VideoSourceCategory>>([]);

    public Task<IReadOnlyList<SearchResult>> GetSourcePageAsync(VideoSource source, int pageStart, int pageSize, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SearchResult>>([]);

    public Task<CategoryVideosResponse> GetCategoryVideosAsync(VideoSource source, string categoryId, int page, int pageSize, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CategoryVideosResponse([], 0, page, 0));

    public Task<IReadOnlyList<SearchResult>> GetHomePageAsync(VideoSource source, string type, string tag, int pageStart, int pageSize, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SearchResult>>([]);
}
