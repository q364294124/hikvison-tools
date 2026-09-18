using System.IO;
using System.Text;

namespace HikDeployTool.Services;

/// <summary>纠错等级。数值即分块表里的行序号（0=L、1=M、2=Q、3=H）。</summary>
internal enum QrEcc
{
    L = 0,
    M = 1,
    Q = 2,
    H = 3,
}

/// <summary>
/// 一个已经编码完成、可以逐格查询的二维码矩阵。
/// 只负责"哪些格子是黑的"，跟怎么画到屏幕上完全解耦 ——
/// 这样同一份矩阵既能渲染给界面、也能存成图片、还能在自检里逐格比对。
/// </summary>
internal sealed class QrCode
{
    private readonly bool[,] _modules;

    internal QrCode(int version, QrEcc ecc, bool[,] modules)
    {
        Version = version;
        Ecc = ecc;
        _modules = modules;
    }

    /// <summary>版本号 1..40。</summary>
    internal int Version { get; }

    internal QrEcc Ecc { get; }

    /// <summary>边长（格数）。版本 v 为 4v+17：v1=21、v40=177。</summary>
    internal int ModuleCount => Version * 4 + 17;

    internal bool IsDark(int row, int col) => _modules[row, col];

    /// <summary>调试/自检用：每行一格，'1' 黑 '0' 白。</summary>
    internal string ToDebugString()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < ModuleCount; r++)
        {
            for (int c = 0; c < ModuleCount; c++) sb.Append(_modules[r, c] ? '1' : '0');
            sb.Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>
/// 纯托管的二维码编码器（QR Code Model 2，ISO/IEC 18004）。
///
/// ---- 为什么必须有这个东西 ----
/// SADP SDK 只把二维码"内容"以字符串形式返回，从来不生成图片。
/// 所以"二维码显示不出来"从来不是 SDK 的问题 —— 上层必须自己把字符串编码成
/// 二维码图案。海康 C++ demo 里那份 QrCodes.cpp 就是这个角色；
/// 这个文件是它的 C# 移植（逐行核对过，不是凭印象重写）。
///
/// ---- 移植时特意改掉的三处 ----
/// 1) glog(0)：demo 的 CQrcodeMath::glog 在 n &lt; 1 时返回 0，
///    等于把"系数 0"算成了"α 的 0 次方 = 1"。它的多项式乘/模里到处用
///    a*v 这种可能为 0 的系数，只是恰好 QR 生成多项式的系数都不为 0 才没暴露。
///    这里改成真正的零判定，结果与标准一致，也不依赖这种巧合。
/// 2) 位缓冲按需扩容：demo 里 realloc 只加了一个增量却按两个增量拷贝，
///    是典型的越界写；C# 这边直接用 List&lt;byte&gt;，没有这回事。
/// 3) 掩码择优的 getLostPoint 里 demo 用 100*dark/count/count 连着两次整除算
///    暗模块比例，千位以下的偏差全被截掉，这一项几乎不起作用。这里按标准
///    改成先算百分比再取整。
///    掩码只影响观感和扫描难度，8 个掩码都是合法解，所以这一点改动不会让
///    图案失去可扫性；代价是"选中的掩码序号"可能和别的编码器不同。
///
/// ---- 输出格式 ----
/// byte 模式（8bit），内容按 UTF-8 取字节。设备码 / GUID / 扫码 URL 全是 ASCII，
/// 走 byte 模式最省心；含中文时绝大多数扫码器也能按 UTF-8 自动识别。
/// </summary>
internal static class QrEncoder
{
    private const int MaxVersion = 40;

    /// <summary>byte 模式的模式指示符。</summary>
    private const int Mode8BitByte = 4;

    private const int Pad0 = 0xEC;
    private const int Pad1 = 0x11;

    private const int G15 = 0x0537;
    private const int G15Mask = 0x5412;
    private const int G18 = 0x1F25;

    // ---- 伽罗华域 GF(256)，本原多项式 x^8+x^4+x^3+x^2+1 (0x11D) ----
    // 表按下标 0..255 建；log 表长度 256 但只用 0..254，
    // log[0] 没有定义（0 没有对数）—— 凡是用到它的地方都必须先判零。
    private static readonly int[] ExpTable = BuildExpTable();
    private static readonly int[] LogTable = BuildLogTable();

    private static int[] BuildExpTable()
    {
        var exp = new int[256];
        for (int i = 0; i < 8; i++) exp[i] = 1 << i;
        for (int i = 8; i < 256; i++)
        {
            exp[i] = exp[i - 4] ^ exp[i - 5] ^ exp[i - 6] ^ exp[i - 8];
        }
        return exp;
    }

    private static int[] BuildLogTable()
    {
        var log = new int[256];
        for (int i = 0; i < 255; i++) log[ExpTable[i]] = i;
        return log;
    }

    /// <summary>α 的 n 次方（n 自动规约到 0..254）。</summary>
    private static int GExp(int n)
    {
        while (n < 0) n += 255;
        while (n >= 255) n -= 255;
        return ExpTable[n];
    }

    /// <summary>
    /// GF(256) 乘法。两个要点都不能省：
    ///   1) 任一因子为 0，积就是 0（0 没有对数，先判零再查表）；
    ///   2) 两个对数相加最大到 254+254=508，超出一个周期就得减 255。
    ///      漏掉第 2 点会在第一个数据码字上就抛"索引超出数组界限"——
    ///      exp 表只有 256 项。
    /// </summary>
    private static int GMul(int a, int b)
    {
        if (a == 0 || b == 0) return 0;

        int sum = LogTable[a] + LogTable[b];
        if (sum >= 255) sum -= 255;
        return ExpTable[sum];
    }

    // =====================================================================
    // 对外入口
    // =====================================================================

    /// <summary>
    /// 把文本编码成二维码，自动挑选"装得下且最小"的版本。
    /// 内容超出 v40 容量时抛 <see cref="ArgumentException"/>。
    /// </summary>
    internal static QrCode Encode(string text, QrEcc ecc = QrEcc.M)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Encode(Encoding.UTF8.GetBytes(text), ecc);
    }

    internal static QrCode Encode(byte[] data, QrEcc ecc = QrEcc.M)
    {
        ArgumentNullException.ThrowIfNull(data);

        int version = ChooseVersion(data.Length, ecc);
        return EncodeAtVersion(data, ecc, version);
    }

    /// <summary>
    /// 指定版本编码。版本必须装得下内容，否则抛异常。
    /// 保留这个重载是为了让自检能"同一内容、逐版本"跑，方便定位是哪个版本的表出问题。
    /// </summary>
    internal static QrCode EncodeAtVersion(byte[] data, QrEcc ecc, int version)
    {
        if (version is < 1 or > MaxVersion) throw new ArgumentOutOfRangeException(nameof(version));

        var buffer = new BitBuffer();
        int lengthBits = version < 10 ? 8 : 16;

        buffer.Put(Mode8BitByte, 4);
        buffer.Put(data.Length, lengthBits);
        foreach (byte b in data) buffer.Put(b, 8);

        int dataCodewords = QrTables.GetDataCodewords(version, (int)ecc);
        if (buffer.BitLength > dataCodewords * 8)
        {
            throw new ArgumentException(
                $"内容超出该版本容量：需要 {buffer.BitLength} 位，版本 {version} 的 {ecc} 级只有 {dataCodewords * 8} 位。");
        }

        byte[] codewords = BuildCodewords(buffer, version, ecc);
        return BuildMatrix(codewords, version, ecc);
    }

    /// <summary>自动选版本：从小到大找第一个装得下的。</summary>
    internal static int ChooseVersion(int byteCount, QrEcc ecc)
    {
        for (int version = 1; version <= MaxVersion; version++)
        {
            int dataCodewords = QrTables.GetDataCodewords(version, (int)ecc);
            int lengthBits = version < 10 ? 8 : 16;
            int needed = 4 + lengthBits + byteCount * 8;
            if (needed <= dataCodewords * 8) return version;
        }

        throw new ArgumentException(
            $"内容太长，超出二维码上限（{byteCount} 字节，{ecc} 级最大 2953 字节）。");
    }

    // =====================================================================
    // 码字构造：数据段填充 → Reed-Solomon → 按分块交错
    // =====================================================================

    private static byte[] BuildCodewords(BitBuffer buffer, int version, QrEcc ecc)
    {
        int dataCodewords = 0;
        foreach (var (count, _, data) in QrTables.GetRsBlocks(version, (int)ecc))
        {
            dataCodewords += count * data;
        }

        int capacityBits = dataCodewords * 8;

        // 结束符：不足 4 位就直接不补（标准允许）
        if (buffer.BitLength + 4 <= capacityBits) buffer.Put(0, 4);

        // 补齐到字节边界
        while (buffer.BitLength % 8 != 0) buffer.Put(0, 1);

        // 填充字节：0xEC / 0x11 交替，直到占满容量
        bool flip = false;
        while (buffer.BitLength < capacityBits)
        {
            buffer.Put(flip ? Pad1 : Pad0, 8);
            flip = !flip;
        }

        byte[] dataBytes = buffer.ToBytes();

        var blocks = new List<(int Total, int Data, byte[] Dc, byte[] Ec)>();
        int offset = 0;
        foreach (var (count, total, dataLen) in QrTables.GetRsBlocks(version, (int)ecc))
        {
            for (int i = 0; i < count; i++)
            {
                var dc = new byte[dataLen];
                Array.Copy(dataBytes, offset, dc, 0, dataLen);
                offset += dataLen;

                int ecLen = total - dataLen;
                blocks.Add((total, dataLen, dc, ComputeEc(dc, ecLen)));
            }
        }

        int totalCodewords = 0;
        foreach (var b in blocks) totalCodewords += b.Total;

        var result = new byte[totalCodewords];
        int index = 0;

        // 数据码字按列交错：先取各块的第一个数据码字，再取第二个……
        int maxData = 0;
        foreach (var b in blocks) maxData = Math.Max(maxData, b.Data);
        for (int i = 0; i < maxData; i++)
        {
            foreach (var b in blocks)
            {
                if (i < b.Data) result[index++] = b.Dc[i];
            }
        }

        // 纠错码字同样交错
        int maxEc = 0;
        foreach (var b in blocks) maxEc = Math.Max(maxEc, b.Ec.Length);
        for (int i = 0; i < maxEc; i++)
        {
            foreach (var b in blocks)
            {
                if (i < b.Ec.Length) result[index++] = b.Ec[i];
            }
        }

        return result;
    }

    /// <summary>生成多项式 g(x)=∏(x-α^i)，i=0..n-1，系数按 x 的降幂排列。</summary>
    private static int[] GeneratorPolynomial(int degree)
    {
        // 从 g(x)=1 开始，每次乘上 (x + α^i)
        int[] poly = { 1 };
        for (int i = 0; i < degree; i++)
        {
            var next = new int[poly.Length + 1];
            int root = ExpTable[i];
            for (int j = 0; j < poly.Length; j++)
            {
                // poly[j] 乘 x → 落在 next[j]；乘 α^i → 落在 next[j+1]
                next[j] ^= poly[j];
                next[j + 1] ^= GMul(poly[j], root);
            }
            poly = next;
        }
        return poly;
    }

    /// <summary>
    /// 算纠错码字：经典 LFSR 除法，等价于取 data·x^n mod g(x)。
    /// 用移位寄存器而不是多项式对象，是因为它天然处理"系数为 0"的情况，
    /// 不需要像 demo 那样在 glog 上做零的特殊约定。
    /// </summary>
    private static byte[] ComputeEc(byte[] data, int ecLength)
    {
        if (ecLength <= 0) return Array.Empty<byte>();

        int[] gen = GeneratorPolynomial(ecLength); // gen[0] 恒为 1，共 ecLength+1 项
        var ec = new byte[ecLength];

        foreach (byte d in data)
        {
            int factor = d ^ ec[0];

            // 寄存器整体左移一格
            for (int i = 0; i < ecLength - 1; i++) ec[i] = ec[i + 1];
            ec[ecLength - 1] = 0;

            if (factor == 0) continue;
            for (int i = 0; i < ecLength; i++)
            {
                ec[i] = (byte)(ec[i] ^ GMul(gen[i + 1], factor));
            }
        }

        return ec;
    }

    // =====================================================================
    // 矩阵构造
    // =====================================================================

    private static QrCode BuildMatrix(byte[] codewords, int version, QrEcc ecc)
    {
        // 掩码择优要反复重画同一张矩阵，所以先挑好再"正式"画一次。
        // 这里不用 demo 的 test=true 试探法（先把功能图案涂成空白再算损失分），
        // 而是每个掩码完整画一遍再算损失分 —— 结果与标准一致，
        // 区别只是把探测矩阵的灰色地带去掉了，代价是 8 倍构图时间（微秒级，无所谓）。
        int bestMask = 0;
        int bestScore = int.MaxValue;

        for (int mask = 0; mask < 8; mask++)
        {
            bool[,] candidate = Compose(version, ecc, codewords, mask);
            int score = LostPoint(candidate, version);
            if (score < bestScore)
            {
                bestScore = score;
                bestMask = mask;
            }
        }

        var modules = Compose(version, ecc, codewords, bestMask);
        return new QrCode(version, ecc, modules);
    }

    /// <summary>用给定掩码完整画一张矩阵（功能图案 + 数据 + 类型信息）。</summary>
    private static bool[,] Compose(int version, QrEcc ecc, byte[] codewords, int maskPattern)
    {
        int count = version * 4 + 17;

        // 0=未占用，1=黑，2=白 —— 必须区分"未占用"和"占用且为白"，
        // 否则数据位会盖掉定位图案留白的部分。
        var state = new byte[count, count];

        SetupPositionProbePattern(state, count, 0, 0);
        SetupPositionProbePattern(state, count, count - 7, 0);
        SetupPositionProbePattern(state, count, 0, count - 7);
        SetupPositionAdjustPattern(state, count, version);
        SetupTimingPattern(state, count);
        SetupTypeInfo(state, count, ecc, maskPattern);
        if (version >= 7) SetupTypeNumber(state, count, version);

        MapData(state, count, codewords, maskPattern);

        var result = new bool[count, count];
        for (int r = 0; r < count; r++)
        {
            for (int c = 0; c < count; c++)
            {
                // 走到这一步不该还有未占用的格子；真有就是构图漏了，按白处理不至于崩
                result[r, c] = state[r, c] == 1;
            }
        }
        return result;
    }

    private static void SetupPositionProbePattern(byte[,] state, int count, int row, int col)
    {
        for (int r = -1; r <= 7; r++)
        {
            for (int c = -1; c <= 7; c++)
            {
                int rr = row + r;
                int cc = col + c;
                if (rr < 0 || rr >= count || cc < 0 || cc >= count) continue;

                bool dark =
                    (r >= 0 && r <= 6 && (c == 0 || c == 6)) ||
                    (c >= 0 && c <= 6 && (r == 0 || r == 6)) ||
                    (r >= 2 && r <= 4 && c >= 2 && c <= 4);

                state[rr, cc] = (byte)(dark ? 1 : 2);
            }
        }
    }

    private static void SetupPositionAdjustPattern(byte[,] state, int count, int version)
    {
        int[] positions = QrTables.GetPatternPositions(version);

        foreach (int row in positions)
        {
            foreach (int col in positions)
            {
                if (state[row, col] != 0) continue; // 与定位图案重叠的跳过

                for (int r = -2; r <= 2; r++)
                {
                    for (int c = -2; c <= 2; c++)
                    {
                        bool dark = r == -2 || r == 2 || c == -2 || c == 2 || (r == 0 && c == 0);
                        state[row + r, col + c] = (byte)(dark ? 1 : 2);
                    }
                }
            }
        }
    }

    private static void SetupTimingPattern(byte[,] state, int count)
    {
        for (int r = 8; r < count - 8; r++)
        {
            if (state[r, 6] != 0) continue;
            state[r, 6] = (byte)(r % 2 == 0 ? 1 : 2);
        }
        for (int c = 8; c < count - 8; c++)
        {
            if (state[6, c] != 0) continue;
            state[6, c] = (byte)(c % 2 == 0 ? 1 : 2);
        }
    }

    private static void SetupTypeInfo(byte[,] state, int count, QrEcc ecc, int maskPattern)
    {
        int data = (FormatEccBits(ecc) << 3) | maskPattern;
        int bits = BchTypeInfo(data);

        for (int i = 0; i < 15; i++)
        {
            bool dark = ((bits >> i) & 1) == 1;

            // 竖向：左侧一列（跳过时序图案所在的行 6）
            if (i < 6) state[i, 8] = (byte)(dark ? 1 : 2);
            else if (i < 8) state[i + 1, 8] = (byte)(dark ? 1 : 2);
            else state[count - 15 + i, 8] = (byte)(dark ? 1 : 2);

            // 横向：上方一行（跳过时序图案所在的列 6）
            if (i < 8) state[8, count - i - 1] = (byte)(dark ? 1 : 2);
            else if (i < 9) state[8, 15 - i] = (byte)(dark ? 1 : 2);
            else state[8, 15 - i - 1] = (byte)(dark ? 1 : 2);
        }

        // 固定黑点
        state[count - 8, 8] = 1;
    }

    private static void SetupTypeNumber(byte[,] state, int count, int version)
    {
        int bits = BchTypeNumber(version);

        for (int i = 0; i < 18; i++)
        {
            bool dark = ((bits >> i) & 1) == 1;
            state[i / 3, i % 3 + count - 8 - 3] = (byte)(dark ? 1 : 2);
            state[i % 3 + count - 8 - 3, i / 3] = (byte)(dark ? 1 : 2);
        }
    }

    private static void MapData(byte[,] state, int count, byte[] codewords, int maskPattern)
    {
        int inc = -1;
        int row = count - 1;
        int bitIndex = 7;
        int byteIndex = 0;

        // 从右下角起，每次两列，蛇形上下走
        for (int col = count - 1; col > 0; col -= 2)
        {
            if (col == 6) col--; // 跳过竖向时序图案那一列

            while (true)
            {
                for (int c = 0; c < 2; c++)
                {
                    int cc = col - c;
                    if (state[row, cc] != 0) continue;

                    bool dark = false;
                    if (byteIndex < codewords.Length)
                    {
                        dark = ((codewords[byteIndex] >> bitIndex) & 1) == 1;
                    }

                    if (GetMask(maskPattern, row, cc)) dark = !dark;

                    state[row, cc] = (byte)(dark ? 1 : 2);

                    bitIndex--;
                    if (bitIndex == -1)
                    {
                        byteIndex++;
                        bitIndex = 7;
                    }
                }

                row += inc;
                if (row < 0 || row >= count)
                {
                    row -= inc;
                    inc = -inc;
                    break;
                }
            }
        }
    }

    private static bool GetMask(int pattern, int i, int j) => pattern switch
    {
        0 => (i + j) % 2 == 0,
        1 => i % 2 == 0,
        2 => j % 3 == 0,
        3 => (i + j) % 3 == 0,
        4 => (i / 2 + j / 3) % 2 == 0,
        5 => (i * j) % 2 + (i * j) % 3 == 0,
        6 => ((i * j) % 2 + (i * j) % 3) % 2 == 0,
        7 => ((i * j) % 3 + (i + j) % 2) % 2 == 0,
        _ => false,
    };

    // =====================================================================
    // 掩码评分（标准里那四条罚分规则）
    // =====================================================================

    private static int LostPoint(bool[,] modules, int version)
    {
        int count = version * 4 + 17;
        int lost = 0;

        // 规则 1：3x3 邻域同色过多
        for (int row = 0; row < count; row++)
        {
            for (int col = 0; col < count; col++)
            {
                int same = 0;
                bool dark = modules[row, col];

                for (int r = -1; r <= 1; r++)
                {
                    int rr = row + r;
                    if (rr < 0 || rr >= count) continue;

                    for (int c = -1; c <= 1; c++)
                    {
                        int cc = col + c;
                        if (cc < 0 || cc >= count) continue;
                        if (r == 0 && c == 0) continue;
                        if (dark == modules[rr, cc]) same++;
                    }
                }

                if (same > 5) lost += 3 + same - 5;
            }
        }

        // 规则 2：2x2 同色块
        for (int row = 0; row < count - 1; row++)
        {
            for (int col = 0; col < count - 1; col++)
            {
                int n = 0;
                if (modules[row, col]) n++;
                if (modules[row + 1, col]) n++;
                if (modules[row, col + 1]) n++;
                if (modules[row + 1, col + 1]) n++;
                if (n == 0 || n == 4) lost += 3;
            }
        }

        // 规则 3：形如 1011101 的假定位图案
        for (int row = 0; row < count; row++)
        {
            for (int col = 0; col < count - 6; col++)
            {
                if (modules[row, col] && !modules[row, col + 1] && modules[row, col + 2] &&
                    modules[row, col + 3] && modules[row, col + 4] && !modules[row, col + 5] &&
                    modules[row, col + 6])
                {
                    lost += 40;
                }
            }
        }
        for (int col = 0; col < count; col++)
        {
            for (int row = 0; row < count - 6; row++)
            {
                if (modules[row, col] && !modules[row + 1, col] && modules[row + 2, col] &&
                    modules[row + 3, col] && modules[row + 4, col] && !modules[row + 5, col] &&
                    modules[row + 6, col])
                {
                    lost += 40;
                }
            }
        }

        // 规则 4：黑白比例偏离 50%
        int darkCount = 0;
        for (int row = 0; row < count; row++)
        {
            for (int col = 0; col < count; col++)
            {
                if (modules[row, col]) darkCount++;
            }
        }

        // 注意：这里按标准应以"百分比"算 —— 乘 100 再除，否则小版本会因为整数截断
        // 永远落在同一档。demo 用的是 100*dark/count/count，两个 count 连着除，
        // 千位以下的差异全被抹掉，等于这一项几乎不起作用。下面按标准写法保留精度。
        int percent = (int)(100.0 * darkCount / count / count);
        lost += Math.Abs(percent - 50) / 5 * 10;

        return lost;
    }

    // =====================================================================
    // BCH 校验
    // =====================================================================

    /// <summary>
    /// 格式信息里那两位"纠错等级指示符"。
    ///
    /// 注意这跟 <see cref="QrEcc"/> 的数值**不是一回事**：
    /// 枚举的数值是"分块表里的行序号"（L,M,Q,H → 0,1,2,3），
    /// 而标准规定的指示符是 L=01、M=00、Q=11、H=10（即 1,0,3,2）。
    /// 这两套顺序恰好错位，直接拿枚举值当指示符写进去，图案看起来完全正常，
    /// 但扫码器读到的纠错等级是错的 —— 它会按错的等级去分块，
    /// 于是整个数据段解成乱码（OpenCV 报的就是"mode 5 不支持"这种莫名其妙的错）。
    /// </summary>
    private static int FormatEccBits(QrEcc ecc) => ecc switch
    {
        QrEcc.L => 1,
        QrEcc.M => 0,
        QrEcc.Q => 3,
        QrEcc.H => 2,
        _ => 0,
    };

    private static int BchDigit(int data)
    {
        int digit = 0;
        while (data != 0)
        {
            digit++;
            data = (int)((uint)data >> 1);
        }
        return digit;
    }

    private static int BchTypeInfo(int data)
    {
        int d = data << 10;
        while (BchDigit(d) - BchDigit(G15) >= 0)
        {
            d ^= G15 << (BchDigit(d) - BchDigit(G15));
        }
        return ((data << 10) | d) ^ G15Mask;
    }

    private static int BchTypeNumber(int data)
    {
        int d = data << 12;
        while (BchDigit(d) - BchDigit(G18) >= 0)
        {
            d ^= G18 << (BchDigit(d) - BchDigit(G18));
        }
        return (data << 12) | d;
    }

    // =====================================================================
    // 渲染
    // =====================================================================

    /// <summary>
    /// 渲染成位图。scale = 每格几像素，quiet = 静区宽度（格）。
    /// 标准要求静区至少 4 格 —— 少了扫码器会认不出边界，这是"图能显示但扫不动"
    /// 最常见的原因，所以默认值给了标准的 4。
    /// </summary>
    internal static System.Windows.Media.Imaging.BitmapSource ToBitmap(QrCode qr, int scale = 8, int quiet = 4)
    {
        if (scale < 1) scale = 1;
        if (quiet < 0) quiet = 0;

        int count = qr.ModuleCount;
        int pixels = (count + quiet * 2) * scale;
        int stride = pixels * 4;

        var buffer = new byte[stride * pixels];
        // 先整片铺白（静区就是白底），再只涂黑格
        for (int i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = 0xFF;     // B
            buffer[i + 1] = 0xFF; // G
            buffer[i + 2] = 0xFF; // R
            buffer[i + 3] = 0xFF; // A
        }

        for (int row = 0; row < count; row++)
        {
            for (int col = 0; col < count; col++)
            {
                if (!qr.IsDark(row, col)) continue;

                int x0 = (col + quiet) * scale;
                int y0 = (row + quiet) * scale;

                for (int y = y0; y < y0 + scale; y++)
                {
                    int baseIdx = y * stride + x0 * 4;
                    for (int x = 0; x < scale; x++)
                    {
                        buffer[baseIdx] = 0x00;
                        buffer[baseIdx + 1] = 0x00;
                        buffer[baseIdx + 2] = 0x00;
                        baseIdx += 4;
                    }
                }
            }
        }

        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
            pixels, pixels, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32,
            null, buffer, stride);

        bmp.Freeze(); // 冻结后可跨线程使用，也能直接绑到 Image 上
        return bmp;
    }

    /// <summary>
    /// 存成 24 位 BMP。故意不走 WPF 的图像编码器：
    /// 这样无界面自检（没有消息循环）也能落盘，方便外部工具解码验证。
    /// </summary>
    internal static void WriteBmp(QrCode qr, string path, int scale = 8, int quiet = 4)
    {
        if (scale < 1) scale = 1;
        if (quiet < 0) quiet = 0;

        int count = qr.ModuleCount;
        int pixels = (count + quiet * 2) * scale;

        // BMP 每行按 4 字节对齐；24 位时即宽度须为 4 的倍数
        int rowBytes = pixels * 3;
        int padding = (4 - rowBytes % 4) % 4;
        int stride = rowBytes + padding;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        int imageSize = stride * pixels;
        int fileSize = 54 + imageSize;

        // BITMAPFILEHEADER
        bw.Write((byte)'B');
        bw.Write((byte)'M');
        bw.Write(fileSize);
        bw.Write(0);            // reserved
        bw.Write(54);           // 数据偏移

        // BITMAPINFOHEADER
        bw.Write(40);           // 结构大小
        bw.Write(pixels);       // 宽
        bw.Write(pixels);       // 高（正数 = 自下而上）
        bw.Write((short)1);     // planes
        bw.Write((short)24);    // 位深
        bw.Write(0);            // 不压缩
        bw.Write(imageSize);
        bw.Write(2835);         // 96 DPI ≈ 2835 px/m
        bw.Write(2835);
        bw.Write(0);            // 调色板数
        bw.Write(0);            // 重要色数

        var row = new byte[stride];
        for (int y = pixels - 1; y >= 0; y--) // 自下而上写
        {
            Array.Clear(row, 0, row.Length);

            int moduleRow = y / scale - quiet;
            for (int x = 0; x < pixels; x++)
            {
                int moduleCol = x / scale - quiet;

                bool dark = moduleRow >= 0 && moduleRow < count &&
                            moduleCol >= 0 && moduleCol < count &&
                            qr.IsDark(moduleRow, moduleCol);

                byte value = dark ? (byte)0x00 : (byte)0xFF;
                int idx = x * 3;
                row[idx] = value;     // B
                row[idx + 1] = value; // G
                row[idx + 2] = value; // R
            }

            bw.Write(row);
        }
    }

    // =====================================================================
    // 位缓冲
    // =====================================================================

    private sealed class BitBuffer
    {
        private byte[] _buffer = new byte[64];

        internal int BitLength { get; private set; }

        internal void Put(int value, int length)
        {
            for (int i = 0; i < length; i++)
            {
                PutBit(((value >> (length - i - 1)) & 1) == 1);
            }
        }

        internal void PutBit(bool bit)
        {
            int index = BitLength / 8;
            if (index >= _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);

            if (bit) _buffer[index] |= (byte)(0x80 >> (BitLength % 8));
            BitLength++;
        }

        internal byte[] ToBytes()
        {
            int len = (BitLength + 7) / 8;
            var result = new byte[len];
            Array.Copy(_buffer, result, len);
            return result;
        }
    }
}
