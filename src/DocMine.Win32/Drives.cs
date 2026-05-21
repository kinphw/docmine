// Win32 드라이브 열거 — Python docmine/drive_picker.py 의 ctypes 코드 1:1 포팅.
//
// .NET 의 DriveInfo.GetDrives() 가 비슷한 정보를 주지만:
//   - 네트워크 드라이브에서 VolumeLabel/RootDirectory 접근 시 예외/지연이 잦음
//   - DriveType enum 의 한국어 라벨 매핑이 필요
//   - 운영 PC 의 폐쇄망 + DRM 환경에서 .NET API 가 막힐 수 있음
// Python 판이 GetLogicalDrives + GetDriveTypeW + GetVolumeInformationW 의
// 3-함수 조합으로 안정적으로 동작 중이라 동일 접근법 채택.

using System.Runtime.InteropServices;
using System.Text;

namespace DocMine.Win32;

public sealed record DriveInfo(string Root, string Label, string DriveTypeName);

public static class Drives
{
    // DRIVE_* 상수 ↔ 한국어 라벨 (drive_picker.py 의 _DRIVE_TYPE_LABEL)
    private static readonly Dictionary<uint, string> DriveTypeLabel = new()
    {
        [0] = "알수없음",
        [1] = "없음",
        [2] = "이동식",
        [3] = "고정",
        [4] = "네트워크",
        [5] = "CD-ROM",
        [6] = "램디스크",
    };

    /// <summary>
    /// 현재 마운트된 드라이브 목록. drive_picker.py:list_drives 와 동일.
    /// 네트워크/CD-ROM 등은 라벨 조회가 느리거나 실패할 수 있어 예외는 무시.
    /// </summary>
    public static IReadOnlyList<DriveInfo> List()
    {
        var bitmask = NativeMethods.GetLogicalDrives();
        var result = new List<DriveInfo>();
        for (var i = 0; i < 26; i++)
        {
            if ((bitmask & (1u << i)) == 0) continue;
            var letter = (char)('A' + i);
            var root = $"{letter}:\\";

            var typeCode = NativeMethods.GetDriveTypeW(root);
            var typeName = DriveTypeLabel.TryGetValue(typeCode, out var n) ? n : "알수없음";

            var label = "";
            try
            {
                var buf = new StringBuilder(261);
                if (NativeMethods.GetVolumeInformationW(
                        root, buf, (uint)buf.Capacity,
                        IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        IntPtr.Zero, 0))
                {
                    label = buf.ToString();
                }
            }
            catch { /* 일부 네트워크/CD-ROM 은 throw 가능 — 무시 */ }

            result.Add(new DriveInfo(root, label, typeName));
        }
        return result;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint GetLogicalDrives();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetDriveTypeW(string lpRootPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetVolumeInformationW(
            string lpRootPathName,
            StringBuilder lpVolumeNameBuffer,
            uint nVolumeNameSize,
            IntPtr lpVolumeSerialNumber,
            IntPtr lpMaximumComponentLength,
            IntPtr lpFileSystemFlags,
            IntPtr lpFileSystemNameBuffer,
            uint nFileSystemNameSize);
    }
}
