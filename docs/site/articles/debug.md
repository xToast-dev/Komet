# Debug timings

`.komet debug` (a client command: the dot, not a slash) writes a protocol of where a frame's time goes.
<xref:Komet.Api.KapiDebug> (`komet.Debug`) puts your own code in it.

```csharp
using (komet.Debug.Measure("grade"))
{
    RenderGrade();
}
```

The timing is booked as `modid:grade`. Outside a capture `Measure` costs a field read and allocates nothing. Work done
only for the protocol can test <xref:Komet.Api.KapiDebug.Capturing> first. Any thread may measure. `modid:what` has at
most 128 characters (`KapiDebug.MaxName`); a capture keeps up to 1024 names.
