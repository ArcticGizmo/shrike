using System.Runtime.InteropServices;

/// <summary>Hand-rolled Media Foundation interop for the encoder spike (vtable slots verified earlier).</summary>
internal static class Mf
{
    public const uint MF_VERSION = 0x00020070;

    public static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    public static void SetGuid(IMFMediaType t, Guid key, Guid value)
    { var k = key; var v = value; t.SetGUID(ref k, ref v); }
    public static void SetU32(IMFMediaType t, Guid key, uint value) { var k = key; t.SetUINT32(ref k, value); }
    public static void SetU64(IMFMediaType t, Guid key, ulong value) { var k = key; t.SetUINT64(ref k, value); }

    public static string Name(int hr) => (uint)hr switch
    {
        0xC00D36B4 => "MF_E_INVALIDMEDIATYPE",
        0xC00D36E6 => "MF_E_ATTRIBUTENOTFOUND",
        0xC00D36B3 => "MF_E_INVALIDSTREAMNUMBER",
        0xC00D36B9 => "MF_E_NO_MORE_TYPES",
        0xC00D6D60 => "MF_E_TRANSFORM_TYPE_NOT_SET",
        0xC00D36B0 => "MF_E_PLATFORM_NOT_INITIALIZED",
        _ => "?",
    };

    [DllImport("mfplat.dll")] public static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] public static extern int MFShutdown();
    [DllImport("mfplat.dll")] public static extern int MFCreateAttributes(out IMFAttributes ppMFAttributes, uint cInitialSize);
    [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IMFMediaType ppMFType);
    [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IMFMediaBuffer ppBuffer);
    [DllImport("mfplat.dll")] public static extern int MFCreateSample(out IMFSample ppIMFSample);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    public static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IMFAttributes attributes, out IMFSinkWriter writer);

    [DllImport("mfplat.dll")]
    public static extern int MFTEnumEx(Guid guidCategory, uint flags, IntPtr pInputType, IntPtr pOutputType,
        out IntPtr pppMFTActivate, out uint pnumMFTActivate);

    [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr ptr);

    public static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4bb9-a5f9-3f5a8c3b6b0d");
    public static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    public static readonly Guid MF_TRANSFORM_ASYNC = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    public static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");

    public const uint MFT_ENUM_FLAG_SYNCMFT = 0x1;
    public const uint MFT_ENUM_FLAG_ASYNCMFT = 0x2;
    public const uint MFT_ENUM_FLAG_HARDWARE = 0x4;
    public const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_REGISTER_TYPE_INFO { public Guid guidMajorType; public Guid guidSubtype; }

    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");

    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-40c6-a4dc-4eb9c740298a");
    public static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
}

[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int _01(); [PreserveSig] int _02(); [PreserveSig] int _03(); [PreserveSig] int _04();
    [PreserveSig] int _05(); [PreserveSig] int _06(); [PreserveSig] int _07(); [PreserveSig] int _08();
    [PreserveSig] int _09(); [PreserveSig] int _10(); [PreserveSig] int _11(); [PreserveSig] int _12();
    [PreserveSig] int _13(); [PreserveSig] int _14(); [PreserveSig] int _15(); [PreserveSig] int _16();
    [PreserveSig] int _17(); [PreserveSig] int _18();
    [PreserveSig] int SetUINT32(ref Guid key, uint value); // 19
    [PreserveSig] int _20(); [PreserveSig] int _21(); [PreserveSig] int _22(); [PreserveSig] int _23();
    [PreserveSig] int _24(); [PreserveSig] int _25(); [PreserveSig] int _26(); [PreserveSig] int _27();
    [PreserveSig] int _28(); [PreserveSig] int _29(); [PreserveSig] int _30();
}

[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType
{
    [PreserveSig] int _01(); [PreserveSig] int _02(); [PreserveSig] int _03(); [PreserveSig] int _04();
    [PreserveSig] int _05(); [PreserveSig] int _06(); [PreserveSig] int _07(); [PreserveSig] int _08();
    [PreserveSig] int _09(); [PreserveSig] int _10(); [PreserveSig] int _11(); [PreserveSig] int _12();
    [PreserveSig] int _13(); [PreserveSig] int _14(); [PreserveSig] int _15(); [PreserveSig] int _16();
    [PreserveSig] int _17(); [PreserveSig] int _18();
    [PreserveSig] int SetUINT32(ref Guid key, uint value);   // 19
    [PreserveSig] int SetUINT64(ref Guid key, ulong value);  // 20
    [PreserveSig] int _21();
    [PreserveSig] int SetGUID(ref Guid key, ref Guid value); // 22
    [PreserveSig] int _23(); [PreserveSig] int _24(); [PreserveSig] int _25(); [PreserveSig] int _26();
    [PreserveSig] int _27(); [PreserveSig] int _28(); [PreserveSig] int _29(); [PreserveSig] int _30();
}

[ComImport, Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFActivate
{
    [PreserveSig] int _01(); [PreserveSig] int _02(); [PreserveSig] int _03(); [PreserveSig] int _04();
    [PreserveSig] int GetUINT32(ref Guid key, out uint value); // 5
    [PreserveSig] int _06(); [PreserveSig] int _07(); [PreserveSig] int _08(); [PreserveSig] int _09();
    [PreserveSig] int _10();
    [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr ppwsz, out uint len); // 11
    [PreserveSig] int _12(); [PreserveSig] int _13(); [PreserveSig] int _14(); [PreserveSig] int _15();
    [PreserveSig] int _16(); [PreserveSig] int _17(); [PreserveSig] int _18(); [PreserveSig] int _19();
    [PreserveSig] int _20(); [PreserveSig] int _21(); [PreserveSig] int _22(); [PreserveSig] int _23();
    [PreserveSig] int _24(); [PreserveSig] int _25(); [PreserveSig] int _26(); [PreserveSig] int _27();
    [PreserveSig] int _28(); [PreserveSig] int _29(); [PreserveSig] int _30();
    [PreserveSig] int ActivateObject(ref Guid riid, out IntPtr ppv); // 31
}

[ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
    [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
    [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
}

[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    [PreserveSig] int _01(); [PreserveSig] int _02(); [PreserveSig] int _03(); [PreserveSig] int _04();
    [PreserveSig] int _05(); [PreserveSig] int _06(); [PreserveSig] int _07(); [PreserveSig] int _08();
    [PreserveSig] int _09(); [PreserveSig] int _10(); [PreserveSig] int _11(); [PreserveSig] int _12();
    [PreserveSig] int _13(); [PreserveSig] int _14(); [PreserveSig] int _15(); [PreserveSig] int _16();
    [PreserveSig] int _17(); [PreserveSig] int _18(); [PreserveSig] int _19(); [PreserveSig] int _20();
    [PreserveSig] int _21(); [PreserveSig] int _22(); [PreserveSig] int _23(); [PreserveSig] int _24();
    [PreserveSig] int _25(); [PreserveSig] int _26(); [PreserveSig] int _27(); [PreserveSig] int _28();
    [PreserveSig] int _29(); [PreserveSig] int _30();
    [PreserveSig] int _31(); [PreserveSig] int _32(); [PreserveSig] int _33();
    [PreserveSig] int SetSampleTime(long hnsSampleTime);          // 34
    [PreserveSig] int _35();
    [PreserveSig] int SetSampleDuration(long hnsSampleDuration);  // 36
    [PreserveSig] int _37(); [PreserveSig] int _38(); [PreserveSig] int _39();
    [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);          // 40
}

[ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSinkWriter
{
    [PreserveSig] int AddStream(IMFMediaType pTargetMediaType, out uint pdwStreamIndex);
    [PreserveSig] int SetInputMediaType(uint dwStreamIndex, IMFMediaType pInputMediaType, IntPtr pEncodingParameters);
    [PreserveSig] int BeginWriting();
    [PreserveSig] int WriteSample(uint dwStreamIndex, IMFSample pSample);
    [PreserveSig] int _05(); [PreserveSig] int _06(); [PreserveSig] int _07(); [PreserveSig] int _08();
    [PreserveSig] int Finalize(); // 9
}
