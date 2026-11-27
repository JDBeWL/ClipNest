// 界面主题、字体与多语言共享应用级静态状态，串行运行避免测试互相污染。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
