---
_layout: landing
_disableContribution: true
title: AsmSdk
---

<div class="asm-landing">

<div class="asm-hero">
  <div class="asm-hero-text">
    <h1>Build x64 and ARM64 assembly with dotnet build.</h1>
    <p class="asm-lede">AsmSdk turns assembly into shared libraries, static libraries and programs
      for Windows, Linux and macOS, shares them as versioned NuGet packages, and keeps them in the
      same solution as your C#, which gets the build of the platform it runs on. The assemblers
      and linkers come with it.</p>
    <div class="asm-actions">
      <a class="asm-btn asm-btn-primary" href="guide/getting-started.md">Get started</a>
      <a class="asm-btn asm-btn-secondary" href="asmproj.md">Read the reference</a>
    </div>
  </div>
  <figure class="asm-byte-a">
    <svg viewBox="236 204 782 746" role="img" aria-labelledby="asm-byte-a-title">
      <title id="asm-byte-a-title">The AsmFoundation A, each block labelled with a machine-code byte from the samples</title>
      <g class="asm-byte-mint">
        <rect x="559" y="214" width="135" height="135"/><text x="626" y="281">8D</text>
        <rect x="414" y="372" width="129" height="125"/><text x="478" y="434">04</text>
        <rect x="559" y="372" width="135" height="125"/><text x="626" y="434">37</text>
        <rect x="712" y="372" width="127" height="125"/><text x="775" y="434">C3</text>
      </g>
      <g class="asm-byte-teal">
        <rect x="349" y="524" width="138" height="121"/><text x="418" y="584">31</text>
        <rect x="766" y="524" width="138" height="121"/><text x="835" y="584">C0</text>
        <rect x="288" y="665" width="139" height="124"/><text x="357" y="727">48</text>
        <rect x="451" y="665" width="351" height="121"/><text x="626" y="725">85 F6 7E</text>
        <rect x="826" y="665" width="139" height="124"/><text x="895" y="727">0F</text>
        <rect x="246" y="811" width="156" height="129"/><text x="324" y="875">4C</text>
        <rect x="850" y="811" width="158" height="129"/><text x="929" y="875">63</text>
      </g>
    </svg>
    <figcaption>The mint blocks are <code>hello_add</code>, assembled for Linux and macOS:
      <code>lea eax, [rdi + rsi]</code> is <code>8D 04 37</code> and <code>ret</code> is
      <code>C3</code>. The teal blocks are the first bytes of <code>sum_i32</code>. Both functions
      come from the samples on this site.</figcaption>
  </figure>
</div>

<section class="asm-band">
  <div class="asm-section">
    <h2>Three files to a shared library</h2>
    <ol class="asm-steps">
      <li>
        <h3>Name the SDK in <code>global.json</code></h3>
<pre><code class="lang-json">{ "msbuild-sdks": {
    "AsmFoundation.Sdk": "0.3.0" } }</code></pre>
      </li>
      <li>
        <h3>Add <code>Hello/Hello.asmproj</code></h3>
<pre><code class="lang-xml">&lt;Project Sdk="AsmFoundation.Sdk"&gt;
  &lt;PropertyGroup&gt;
    &lt;AssemblyName&gt;hello&lt;/AssemblyName&gt;
    &lt;RuntimeIdentifiers&gt;win-x64;linux-x64;osx-x64&lt;/RuntimeIdentifiers&gt;
  &lt;/PropertyGroup&gt;
&lt;/Project&gt;</code></pre>
      </li>
      <li>
        <h3>Run <code>dotnet build</code></h3>
<pre><code class="lang-text">Hello -&gt; bin/Debug/freestanding/win-x64/hello.dll
Hello -&gt; bin/Debug/freestanding/linux-x64/libhello.so
Hello -&gt; bin/Debug/freestanding/osx-x64/libhello.dylib</code></pre>
        <p>Every <code>*.asm</code> next to the project is assembled for each x64 platform, the way
          every <code>*.cs</code> is compiled in a <code>.csproj</code>. A <code>*.s</code> file adds
          ARM64. <a href="guide/getting-started.md">Walk through it</a>.</p>
      </li>
    </ol>
  </div>
</section>

<section class="asm-section">
  <h2>What a build does</h2>
  <div class="asm-map" role="img" aria-label="Sources and references go into dotnet build, which restores the toolchain, assembles and links; the outputs are libraries, executables and packages used by C#, Python and other assembly projects.">
    <div class="asm-map-col">
      <h3>You write</h3>
      <div class="asm-map-item"><code>*.asm</code>, <code>*.s</code>, <code>include/*.inc</code><span>x64 and ARM64 sources and public include files</span></div>
      <div class="asm-map-item"><code>&lt;ProjectReference&gt;</code><span>other assembly projects</span></div>
      <div class="asm-map-item"><code>&lt;PackageReference&gt;</code><span>assembly libraries, by version</span></div>
    </div>
    <div class="asm-map-arrow" aria-hidden="true"></div>
    <div class="asm-map-sdk">
      <div class="asm-map-sdk-title"><img src="images/logo.svg" alt="" width="22" height="22"> dotnet build</div>
      <div class="asm-map-stage"><b>Restore</b><span>AsmFoundation.Toolchain for this machine, from NuGet</span></div>
      <div class="asm-map-stage"><b>Assemble</b><span>NASM 3.02 and llvm-mc 23.1.5, per platform, one run per changed source</span></div>
      <div class="asm-map-stage"><b>Link</b><span>lld 23.1.5 and llvm-ar, with deterministic output</span></div>
    </div>
    <div class="asm-map-arrow" aria-hidden="true"></div>
    <div class="asm-map-col">
      <h3>You get</h3>
      <div class="asm-map-item"><code>hello.dll</code>, <code>libhello.so</code>, <code>libhello.dylib</code><span>shared libraries</span></div>
      <div class="asm-map-item"><code>hello.lib</code>, <code>libhello.a</code><span>static libraries</span></div>
      <div class="asm-map-item"><code>hello-app.exe</code>, <code>hello-app</code><span>programs</span></div>
      <div class="asm-map-item"><code>Hello.1.0.0.nupkg</code><span>from dotnet pack</span></div>
    </div>
    <div class="asm-map-arrow" aria-hidden="true"></div>
    <div class="asm-map-col">
      <h3>Used by</h3>
      <div class="asm-map-item">C# through <code>[LibraryImport]</code></div>
      <div class="asm-map-item">Python through <code>ctypes</code></div>
      <div class="asm-map-item">other assembly projects and packages</div>
      <div class="asm-map-item"><a href="ecosystem/index.md">KvAsm and GrpcAsm</a></div>
    </div>
  </div>
</section>

<section class="asm-section asm-two">
  <div>
    <h2><a href="ecosystem/index.md">The ecosystem</a></h2>
    <dl class="asm-list">
      <dt>KvAsm</dt><dd>A gRPC key-value server in assembly. Its unmodified source builds with AsmSdk.</dd>
      <dt>GrpcAsm</dt><dd>A gRPC server and client in assembly, built with AsmSdk: its NASM, C and .NET APIs work today, Python is next (in development).</dd>
      <dt>asmsdk for Claude Code</dt><dd>A plugin that teaches Claude Code the SDK, with a scaffolder, a linter and an ELF inspector.</dd>
    </dl>
  </div>
  <div>
    <h2><a href="toolchain/index.md">The toolchain</a></h2>
    <dl class="asm-list">
      <dt>NASM 3.02</dt><dd>Assembles the x64 sources. The official binary on Windows, built from source on Linux and macOS.</dd>
      <dt>llvm-mc 23.1.5</dt><dd>Assembles the GNU-syntax sources: ARM64.</dd>
      <dt>lld 23.1.5</dt><dd>Links for Windows, Linux and macOS: lld-link, ld.lld and ld64.lld in one program.</dd>
      <dt>llvm-ar, llvm-readobj</dt><dd>Write deterministic archives and inspect what came out.</dd>
    </dl>
  </div>
</section>

</div>
