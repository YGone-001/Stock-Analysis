global using AIHelper.Core.StockData;
global using Xunit;

// Several legacy scanner tests write timestamp-named files in one shared output directory.
// Serial test collections prevent same-millisecond filename collisions without altering scanner behavior.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
