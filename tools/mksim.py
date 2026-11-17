import os, re
from edlib import edit, ROOT

tests = os.path.join(ROOT, "Malx_AI.Tests", "Malx_AI.Tests.csproj")
text = open(tests, encoding="utf-8").read()
if "ComputerUseSessionController.Prompts.cs" not in text:
    edit("Malx_AI.Tests/Malx_AI.Tests.csproj", [
        ("""    <Compile Include="..\\Malx_AI\\ComputerUse\\ComputerUseSessionController.State.cs" Link="Production\\ComputerUse\\ComputerUseSessionController.State.cs" />""",
         """    <Compile Include="..\\Malx_AI\\ComputerUse\\ComputerUseSessionController.State.cs" Link="Production\\ComputerUse\\ComputerUseSessionController.State.cs" />
    <Compile Include="..\\Malx_AI\\ComputerUse\\ComputerUseSessionController.Prompts.cs" Link="Production\\ComputerUse\\ComputerUseSessionController.Prompts.cs" />"""),
    ])
    text = open(tests, encoding="utf-8").read()

here = os.path.dirname(os.path.abspath(__file__))
sim = os.path.join(here, "axsim")
os.makedirs(sim, exist_ok=True)
includes = re.findall(r'<Compile Include="\.\.\\(Malx_AI\\[^"]+)"', text)
extra = [
    r"Malx_AI\ComputerUse\ComputerUseScreenCapture.cs",
    r"Malx_AI\ComputerUse\ComputerUseUiCatalog.cs",
    r"Malx_AI\ComputerUse\ComputerUseNativeInput.cs",
]
lines = []
for rel in includes + [e for e in extra if e not in includes]:
    lines.append('    <Compile Include="%s" Link="Production\\%s" />' % (os.path.join(ROOT, rel), rel[len("Malx_AI\\"):]))

csproj = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>axsim</AssemblyName>
    <RootNamespace>AxSim</RootNamespace>
    <NoWarn>$(NoWarn);CA1416;CS8618;CS8602;CS8604</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="HtmlAgilityPack" Version="1.12.4" />
    <PackageReference Include="Markdig" Version="0.37.0" />
    <PackageReference Include="System.Drawing.Common" Version="10.0.0" />
  </ItemGroup>
  <ItemGroup>
    <Using Remove="System.Drawing" />
    <Using Remove="System.Drawing.Imaging" />
    <Using Include="System.IO" />
    <Using Include="System.Linq" />
    <Using Include="System.Net.Http" />
    <Using Include="System.Threading" />
    <Using Include="System.Threading.Tasks" />
  </ItemGroup>
  <ItemGroup>
%s
  </ItemGroup>
</Project>
""" % "\n".join(lines)
open(os.path.join(sim, "axsim.csproj"), "w", encoding="utf-8").write(csproj)
print("linked", len(lines))
