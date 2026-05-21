// Windows Job Object — Python inserter.py 의 _setup_kill_on_close_job 등가.
//
// 부모 프로세스(메인 GUI) 가 어떻게 죽든 자식 워커도 함께 종료되도록.
// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE 를 켜둔 Job 에 현재 프로세스를 할당하면,
// 부모가 사라지는 순간 커널이 그 Job 의 모든 프로세스를 즉시 종료한다.
// Process.Start 로 띄운 자식들은 별다른 옵션 없이 부모의 Job 을 자동 상속.
//
// Python 판 주석 참조:
//   - mp.Process 의 daemon=True 는 atexit 훅 기반이라 GUI 강제종료/Task Manager
//     같은 비정상 종료에서는 워커 고아화. Job Object 가 커널 차원 안전망.
//   - HWP 워커가 띄우는 Hwp.exe 는 DCOM 활성화 경로에 따라 Job 을 벗어날 수 있어
//     taskkill 보조 안전망 별도 유지 (HwpWorker Phase 에서 처리).

using System.Runtime.InteropServices;

namespace DocMine.Win32;

public static class JobObject
{
    // GC 되면 효과가 사라지므로 정적 변수로 보관.
    // 동일 프로세스에서 두 번 이상 호출되어도 첫 호출만 적용.
    private static IntPtr _jobHandle = IntPtr.Zero;
    private static readonly object _lock = new();

    /// <summary>
    /// 현재 프로세스를 KILL_ON_JOB_CLOSE Job 에 할당.
    /// 이미 다른 Job 에 속해 있어 실패하면 silent 종료 (디버거/샌드박스 환경).
    /// </summary>
    public static bool SetupKillOnClose()
    {
        lock (_lock)
        {
            if (_jobHandle != IntPtr.Zero) return true;

            try
            {
                var job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return false;

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                    }
                };

                var len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var ptr = Marshal.AllocHGlobal(len);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)len))
                    {
                        CloseHandle(job);
                        return false;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }

                if (!AssignProcessToJobObject(job, GetCurrentProcess()))
                {
                    CloseHandle(job);
                    return false;
                }

                _jobHandle = job;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    // ─ P/Invoke ─────────────────────────────────────────────────────

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int  JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long  PerProcessUserTimeLimit;
        public long  PerJobUserTimeLimit;
        public uint  LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint  ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint  PriorityClass;
        public uint  SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
