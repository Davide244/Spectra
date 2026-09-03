using Xunit;

// This suite drives REAL drivers, and two of its collections bring up a
// graphics device: GlRendererCollection creates a GLFW window with an OpenGL
// context, D3DDeviceCollection creates D3D11 and D3D12 devices. xUnit runs
// separate collections in parallel, so those two creations raced, and the way
// the race presented was D3D11CreateDevice reporting success and handing back
// no device: 28 failures in one run out of four, every one of them in the
// collection that happened to lose, none of them in whatever had just been
// edited. It reproduces on an untouched checkout, so it is not a regression
// anybody introduced and cannot be bisected to a commit.
//
// Serialising the whole assembly is the honest fix rather than a workaround.
// The engine creates exactly one device per process, so nothing here is giving
// up coverage of a case the product has: concurrent device creation was only
// ever an artifact of the test runner. The whole suite costs about 1.7 s
// serial against 0.9 s parallel, so the saving was never worth a gate that
// fails one run in four.
//
// D3D11Renderer.EnsureDeviceCreated now refuses that null device by name, so
// if this ever happens for real the message says what went wrong.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
