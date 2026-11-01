# Debug timings

`.komet debug` (a client command: the dot, not a slash) writes a protocol of where a frame's time goes. <xref:Komet.Api.KometDebug> puts your own code in
it.

```csharp
using (KometDebug.Measure("mymod:grade"))
{
    RenderGrade();
}
```

Outside a capture `Measure` costs a field read and books nothing. Work done only for the protocol can test
<xref:Komet.Api.KometDebug.Capturing> first. Any thread may measure. Names are `modid:what`, at most 128
characters; a capture keeps up to 1024 names.
