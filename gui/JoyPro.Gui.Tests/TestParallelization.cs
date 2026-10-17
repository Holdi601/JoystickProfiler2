using Xunit;

// Avalonia is single-threaded: render tests (RenderTargetBitmap) and view model tests that create controls must not
// run at the same time on different threads. In parallel, AvaloniaProperty's metadata cache (a plain Dictionary) was
// written from two threads, a render test failed with a NullReferenceException inside it and the test host crashed
// later (dotnet test then reported "Passed" for only the tests that had run). One test at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
