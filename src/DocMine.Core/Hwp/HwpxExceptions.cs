// Python hwp_parser.py 의 HWPXError 계열 1:1 포팅.
// HwpxDrmError 가 가장 중요 — ZipDocReader 가 던지면 호출자가 ComDocReader 로 fallback.

namespace DocMine.Core.Hwp;

public class HwpxError       : Exception { public HwpxError(string m) : base(m) { } public HwpxError(string m, Exception inner) : base(m, inner) { } }
public class HwpxFormatError : HwpxError { public HwpxFormatError(string m) : base(m) { } }
public class HwpxParseError  : HwpxError { public HwpxParseError(string m, Exception inner) : base(m, inner) { } }
public class HwpxDrmError    : HwpxError { public HwpxDrmError(string m) : base(m) { } public HwpxDrmError(string m, Exception inner) : base(m, inner) { } }
public class HwpxComError    : HwpxError { public HwpxComError(string m) : base(m) { } public HwpxComError(string m, Exception inner) : base(m, inner) { } }
