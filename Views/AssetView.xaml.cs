using System.Windows.Controls;
using System.Windows.Threading;
using HikDeployTool.Services;

namespace HikDeployTool.Views;

/// <summary>资产台账页。</summary>
public partial class AssetView : UserControl
{
    public AssetView() => InitializeComponent();

    /// <summary>
    /// 表格里改完一格就顺手落盘。
    ///
    /// 台账是最终交付物，用户填完安装位置最可能直接关程序走人——
    /// 不在这一刻保存，刚敲进去的内容就白填了。
    /// 注意 CellEditEnding 触发时绑定源尚未更新，所以要投递到 Background 再存，
    /// 否则存下去的还是旧值。
    /// </summary>
    private void AssetGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                AppState.Current.SaveAssets();
            }
            catch (Exception ex)
            {
                // 存盘失败不能把编辑动作打断，只记一条日志
                LogService.Current.Warn($"台账自动保存失败：{ex.Message}", "台账");
            }
        }));
    }
}
