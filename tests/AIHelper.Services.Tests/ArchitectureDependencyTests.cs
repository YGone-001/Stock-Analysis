using System.Reflection;
using System.Net;
using System.Net.Http;
using AIHelper;
using AIHelper.Helpers;
using AIHelper.Models;
using AIHelper.Services.StockData;
using AIHelper.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class ArchitectureDependencyTests
{
    [Theory]
    [InlineData("AIHelper.Infrastructure")]
    [InlineData("AIHelper.Services")]
    [InlineData("AIHelper.ViewModels")]
    [InlineData("AIHelper")]
    public void Core_DoesNotReference_OuterLayers(string forbiddenAssembly) =>
        AssertDoesNotReference(typeof(StockModel).Assembly, forbiddenAssembly);

    [Theory]
    [InlineData("AIHelper.ViewModels")]
    [InlineData("AIHelper")]
    public void Services_DoesNotReference_PresentationLayers(string forbiddenAssembly) =>
        AssertDoesNotReference(typeof(StockDataGateway).Assembly, forbiddenAssembly);

    [Theory]
    [InlineData("AIHelper.Services")]
    [InlineData("AIHelper.ViewModels")]
    [InlineData("AIHelper")]
    public void Infrastructure_DoesNotReference_HigherLayers(string forbiddenAssembly) =>
        AssertDoesNotReference(typeof(NetworkHelper).Assembly, forbiddenAssembly);

    [Fact]
    public void CompositionRoot_Resolves_Main_Stock_And_Sparrow_ViewModels_WithoutNetworkAccess()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        services.AddSingleton<SmokeStockDataGateway>();
        services.AddSingleton<IStockDataProvider>(sp => sp.GetRequiredService<SmokeStockDataGateway>());
        services.AddSingleton<IStockDataGateway>(sp => sp.GetRequiredService<SmokeStockDataGateway>());
        services.AddSingleton<IHttpClientFactory, SmokeHttpClientFactory>();

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using MainViewModel mainViewModel = provider.GetRequiredService<MainViewModel>();

        Assert.NotNull(mainViewModel.DataGateway);
        Assert.IsType<QuoteService>(provider.GetRequiredService<IQuoteService>());
        Assert.IsType<KlineService>(provider.GetRequiredService<IKlineService>());
        Assert.IsType<MarketCalendarService>(provider.GetRequiredService<IMarketCalendarService>());
        Assert.IsType<DefaultDataSourcePolicyProvider>(provider.GetRequiredService<IDataSourcePolicyProvider>());
        Assert.IsType<ProviderHealthService>(provider.GetRequiredService<IProviderHealthService>());
        Assert.IsType<StockViewModel>(provider.GetRequiredService<StockViewModel>());
        Assert.IsType<SparrowViewModel>(provider.GetRequiredService<SparrowViewModel>());
    }

    private static void AssertDoesNotReference(Assembly assembly, string forbiddenAssembly)
    {
        Assert.DoesNotContain(
            assembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, forbiddenAssembly, StringComparison.Ordinal));
    }

    private sealed class SmokeStockDataGateway : IStockDataGateway
    {
        public event Action<StockDataResult>? StockDataStatusChanged
        {
            add { }
            remove { }
        }

        public bool CanHandle(StockDataRequest request) => true;

        public Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StockDataResult
            {
                Endpoint = request.Endpoint,
                Handled = true,
                Success = false,
                Source = "Smoke",
                Error = "Network disabled for composition-root smoke test."
            });

        public Task<StockNameCacheSnapshot> GetStockNameCacheSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StockNameCacheSnapshot());

        public Task MergeStockNameCacheAsync(IReadOnlyDictionary<string, string> items, string source, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> GetEastMoneyAsync(Uri uri, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
    }

    private sealed class SmokeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SmokeHttpMessageHandler());
    }

    private sealed class SmokeHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    }
}
