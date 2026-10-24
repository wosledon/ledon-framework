// 各用例共享 Trace 静态轨迹，必须串行执行
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
