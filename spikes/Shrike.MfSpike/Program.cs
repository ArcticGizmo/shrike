using System.Buffers.Binary;
using System.Runtime.InteropServices;

// Spike: can the Media Foundation Sink Writer encode an H.264 MP4 on THIS machine?
// The earlier failure was MF_E_ATTRIBUTENOTFOUND from the encoder's SetOutputType. A known-working
// hardware-encoder sample sets MF_MT_ALL_SAMPLES_INDEPENDENT on the output type, which we never did —
// this probes whether that (and a couple of variants) unblocks the simple sink-writer path.

const int W = 640, H = 480, Fps = 30, Frames = 30;

Check(Mf.MFStartup(Mf.MF_VERSION, 0), "MFStartup");

Anim.Check();

Enumerate();

Console.WriteLine($"\n== MF sink-writer H.264 probe ({W}x{H}@{Fps}, {Frames} frames) ==\n");

TryVariant("baseline (no ALL_SAMPLES_INDEPENDENT)", allSamplesIndependent: false, hwTransforms: false);
TryVariant("output + ALL_SAMPLES_INDEPENDENT", allSamplesIndependent: true, hwTransforms: false);
TryVariant("ALL_SAMPLES_INDEPENDENT + hw transforms", allSamplesIndependent: true, hwTransforms: true);

Mf.MFShutdown();
Console.WriteLine("\n== done ==");
return;

static void Enumerate()
{
    EnumEncoders("H.264 encoders", filterH264: true);
    EnumEncoders("ALL video encoders", filterH264: false);
}

static void EnumEncoders(string label, bool filterH264)
{
    Console.WriteLine($"== {label} (MFTEnumEx) ==");
    var pInfo = IntPtr.Zero;
    if (filterH264)
    {
        var info = new Mf.MFT_REGISTER_TYPE_INFO { guidMajorType = Mf.MFMediaType_Video, guidSubtype = Mf.MFVideoFormat_H264 };
        pInfo = Marshal.AllocHGlobal(Marshal.SizeOf<Mf.MFT_REGISTER_TYPE_INFO>());
        Marshal.StructureToPtr(info, pInfo, false);
    }
    try
    {
        var flags = Mf.MFT_ENUM_FLAG_SYNCMFT | Mf.MFT_ENUM_FLAG_ASYNCMFT | Mf.MFT_ENUM_FLAG_HARDWARE | Mf.MFT_ENUM_FLAG_SORTANDFILTER;
        var hr = Mf.MFTEnumEx(Mf.MFT_CATEGORY_VIDEO_ENCODER, flags, IntPtr.Zero, pInfo, out var ppActivate, out var count);
        if (hr < 0) { Console.WriteLine($"  MFTEnumEx failed: 0x{hr:X8}"); return; }
        Console.WriteLine($"  found {count} encoder(s):");

        for (var i = 0; i < count; i++)
        {
            var pUnk = Marshal.ReadIntPtr(ppActivate, i * IntPtr.Size);
            var activate = (IMFActivate)Marshal.GetObjectForIUnknown(pUnk);

            var nameKey = Mf.MFT_FRIENDLY_NAME_Attribute;
            var name = "(no name)";
            if (activate.GetAllocatedString(ref nameKey, out var pName, out _) >= 0)
            {
                name = Marshal.PtrToStringUni(pName) ?? name;
                Mf.CoTaskMemFree(pName);
            }

            var asyncKey = Mf.MF_TRANSFORM_ASYNC;
            var isAsync = activate.GetUINT32(ref asyncKey, out var a) >= 0 && a != 0;

            Console.WriteLine($"   [{i}] {name}  (async={isAsync})");

            Marshal.ReleaseComObject(activate);
            Marshal.Release(pUnk);
        }
        Mf.CoTaskMemFree(ppActivate);
    }
    finally { if (pInfo != IntPtr.Zero) Marshal.FreeHGlobal(pInfo); }
}

void TryVariant(string label, bool allSamplesIndependent, bool hwTransforms)
{
    var path = Path.Combine(Path.GetTempPath(), $"mfspike-{Guid.NewGuid():N}.mp4");
    Console.WriteLine($"--- {label} ---");
    try
    {
        var hr = Encode(path, allSamplesIndependent, hwTransforms, out var step);
        if (hr < 0)
        {
            Console.WriteLine($"  FAILED at {step}: 0x{hr:X8} ({Mf.Name(hr)})\n");
            return;
        }

        var data = File.ReadAllBytes(path);
        var ok = data.Length > 2000
                 && System.Text.Encoding.ASCII.GetString(data, 4, 4) == "ftyp"
                 && FindBox(data, "moov");
        Console.WriteLine($"  SUCCESS: {data.Length:N0} bytes, well-formed MP4 = {ok}\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  EXCEPTION: {ex.Message}\n");
    }
    finally
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

static int Encode(string path, bool allSamplesIndependent, bool hwTransforms, out string step)
{
    step = "create";
    int hr;

    IMFAttributes? attrs = null;
    if (hwTransforms)
    {
        if ((hr = Mf.MFCreateAttributes(out attrs, 1)) < 0) { step = "MFCreateAttributes"; return hr; }
        var k = Mf.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS;
        attrs!.SetUINT32(ref k, 1);
    }

    if ((hr = Mf.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs!, out var writer)) < 0)
    { step = "MFCreateSinkWriterFromURL"; return hr; }

    // Output H.264 type.
    Mf.MFCreateMediaType(out var outT);
    Mf.SetGuid(outT, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video);
    Mf.SetGuid(outT, Mf.MF_MT_SUBTYPE, Mf.MFVideoFormat_H264);
    Mf.SetU32(outT, Mf.MF_MT_AVG_BITRATE, 4_000_000);
    Mf.SetU32(outT, Mf.MF_MT_INTERLACE_MODE, 2);
    Mf.SetU32(outT, Mf.MF_MT_MPEG2_PROFILE, 66);
    Mf.SetU64(outT, Mf.MF_MT_FRAME_SIZE, Mf.Pack(W, H));
    Mf.SetU64(outT, Mf.MF_MT_FRAME_RATE, Mf.Pack(Fps, 1));
    Mf.SetU64(outT, Mf.MF_MT_PIXEL_ASPECT_RATIO, Mf.Pack(1, 1));
    if (allSamplesIndependent) Mf.SetU32(outT, Mf.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);

    if ((hr = writer.AddStream(outT, out var streamIndex)) < 0) { step = "AddStream"; return hr; }

    // Input NV12 type.
    Mf.MFCreateMediaType(out var inT);
    Mf.SetGuid(inT, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video);
    Mf.SetGuid(inT, Mf.MF_MT_SUBTYPE, Mf.MFVideoFormat_NV12);
    Mf.SetU32(inT, Mf.MF_MT_INTERLACE_MODE, 2);
    Mf.SetU64(inT, Mf.MF_MT_FRAME_SIZE, Mf.Pack(W, H));
    Mf.SetU64(inT, Mf.MF_MT_FRAME_RATE, Mf.Pack(Fps, 1));
    Mf.SetU64(inT, Mf.MF_MT_PIXEL_ASPECT_RATIO, Mf.Pack(1, 1));

    if ((hr = writer.SetInputMediaType(streamIndex, inT, IntPtr.Zero)) < 0) { step = "SetInputMediaType"; return hr; }
    if ((hr = writer.BeginWriting()) < 0) { step = "BeginWriting"; return hr; }

    var nv12Len = W * H + W * H / 2;
    var durationHns = 10_000_000L / Fps;
    for (var i = 0; i < Frames; i++)
    {
        var nv12 = Nv12(MovingFrame(i), W, H);
        Mf.MFCreateMemoryBuffer((uint)nv12Len, out var buf);
        buf.Lock(out var ptr, out _, out _);
        Marshal.Copy(nv12, 0, ptr, nv12Len);
        buf.Unlock();
        buf.SetCurrentLength((uint)nv12Len);

        Mf.MFCreateSample(out var sample);
        sample.AddBuffer(buf);
        sample.SetSampleTime(i * durationHns);
        sample.SetSampleDuration(durationHns);
        if ((hr = writer.WriteSample(streamIndex, sample)) < 0) { step = $"WriteSample[{i}]"; return hr; }

        Marshal.ReleaseComObject(sample);
        Marshal.ReleaseComObject(buf);
    }

    if ((hr = writer.Finalize()) < 0) { step = "Finalize"; return hr; }

    Marshal.ReleaseComObject(inT);
    Marshal.ReleaseComObject(outT);
    Marshal.ReleaseComObject(writer);
    if (attrs is not null) Marshal.ReleaseComObject(attrs);
    return 0;
}

static byte[] MovingFrame(int i)
{
    var buf = new byte[W * H * 4];
    byte b = (byte)(i * 8), g = (byte)(255 - i * 8), r = 80;
    for (var p = 0; p < buf.Length; p += 4) { buf[p] = b; buf[p + 1] = g; buf[p + 2] = r; buf[p + 3] = 255; }
    return buf;
}

static byte[] Nv12(byte[] bgra, int w, int h)
{
    var outp = new byte[w * h + w * h / 2];
    var uvBase = w * h;
    for (var y = 0; y < h; y++)
    for (var x = 0; x < w; x++)
    {
        var i = (y * w + x) * 4;
        int bb = bgra[i], gg = bgra[i + 1], rr = bgra[i + 2];
        outp[y * w + x] = Clamp(((66 * rr + 129 * gg + 25 * bb + 128) >> 8) + 16);
    }
    for (var by = 0; by < h; by += 2)
    for (var bx = 0; bx < w; bx += 2)
    {
        var i = (by * w + bx) * 4;
        int bb = bgra[i], gg = bgra[i + 1], rr = bgra[i + 2];
        var uv = uvBase + (by / 2) * w + bx;
        outp[uv] = Clamp(((-38 * rr - 74 * gg + 112 * bb + 128) >> 8) + 128);
        outp[uv + 1] = Clamp(((112 * rr - 94 * gg - 18 * bb + 128) >> 8) + 128);
    }
    return outp;
    static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}

static bool FindBox(byte[] data, string fourcc)
{
    var pos = 0;
    while (pos + 8 <= data.Length)
    {
        var size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
        if (System.Text.Encoding.ASCII.GetString(data, pos + 4, 4) == fourcc) return true;
        if (size < 8) break;
        pos += (int)size;
    }
    return false;
}

static void Check(int hr, string what)
{
    if (hr < 0) throw new InvalidOperationException($"{what}: 0x{hr:X8}");
}
