using System;
using System.IO;
using System.Text;
using AIHelper.Core.Sparrow;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowMarketDataCachePersistenceTests
{
    [Fact]
    public void PersistAndReload_RoundTripsKlineEntries()
    {
        string file = TempFile();
        try
        {
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var first = new SparrowMarketDataCache(time, persistFile: file);
            string key = SparrowDataCachePolicy.GetKlineKey("600000", 120, "day");
            first.Set(key, "{\"data\":[]}", TimeSpan.FromDays(1));
            first.Flush();

            var second = new SparrowMarketDataCache(time, persistFile: file);
            Assert.True(second.TryGet(key, useCache: true, out string? value));
            Assert.Equal("{\"data\":[]}", value);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void PersistAndReload_SkipsExpiredEntries()
    {
        string file = TempFile();
        try
        {
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var first = new SparrowMarketDataCache(time, persistFile: file);
            string key = SparrowDataCachePolicy.GetKlineKey("600000", 120, "day");
            first.Set(key, "{\"data\":[]}", TimeSpan.FromMinutes(5));
            first.Flush();

            time.Advance(TimeSpan.FromMinutes(6));

            var second = new SparrowMarketDataCache(time, persistFile: file);
            Assert.False(second.TryGet(key, useCache: true, out _));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void PersistAndReload_DoesNotPersistNonKlineEntries()
    {
        string file = TempFile();
        try
        {
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var first = new SparrowMarketDataCache(time, persistFile: file);
            string quoteKey = SparrowDataCachePolicy.GetQuoteKey(new[] { "600000" });
            first.Set(quoteKey, "{\"quote\":1}", TimeSpan.FromDays(1));
            first.Flush();

            var second = new SparrowMarketDataCache(time, persistFile: file);
            Assert.False(second.TryGet(quoteKey, useCache: true, out _));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void LoadFromCorruptFile_DoesNotThrow()
    {
        string file = TempFile();
        try
        {
            File.WriteAllText(file, "{ not valid json", Encoding.UTF8);
            var cache = new SparrowMarketDataCache(persistFile: file);
            Assert.Equal(0, cache.Count);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "sparrow-cache-test-" + Guid.NewGuid().ToString("N") + ".json");

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset initial) => _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
