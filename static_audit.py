"""Non-compiler checks. NEVER replaces a real C# build or security assessment."""
from pathlib import Path
from datetime import datetime,timezone
import json,re,xml.etree.ElementTree as ET
root=Path(__file__).resolve().parent
checks=[]
def ck(name, ok, detail=''):
    checks.append(dict(name=name,pass_check=bool(ok),detail=str(detail)))
excluded={'bin','obj','publish','extracted','sandbox-output','.git','.dotnet'}
def sources(pattern):
    return sorted(x for x in root.rglob(pattern) if not excluded.intersection(x.relative_to(root).parts))
files=sources('*.cs')
ck('C# source files',len(files)>=10,len(files))
# C# syntax is checked by dotnet build -warnaserror, not by a heuristic lexer.
for f in sources('*.xaml')+sources('*.csproj')+sources('*.slnx')+sources('*.manifest')+[root/'Directory.Build.props']:
    try: ET.parse(f);ok=True; msg='valid XML'
    except ET.ParseError as e:ok=False;msg=str(e)
    ck('XML '+str(f.relative_to(root)),ok,msg)
xaml=(root/'src/SpeedForge.Desktop/MainWindow.xaml').read_text(encoding='utf-8-sig')
code='\n'.join(x.read_text(encoding='utf-8-sig') for x in (root/'src/SpeedForge.Desktop').glob('MainWindow*.cs'))
handlers=[v for element in ET.fromstring(xaml).iter() for k,v in element.attrib.items()
          if k in {'Click','SelectionChanged','TextChanged','Checked','Unchecked','Closing','SizeChanged','MouseDoubleClick'}]
ck('All XAML event handlers exist',all(re.search(r'\b'+re.escape(h)+r'\s*\(',code) for h in handlers),handlers)
try:
    catalog=json.loads((root/'src/SpeedForge.Core/servers.json').read_text(encoding='utf-8-sig'))
    ck('Catalog JSON',len(catalog)>=65 and all(1<=x['port']<=65535 for x in catalog),'count='+str(len(catalog)))
    ck('Catalog has sources and provenance', all(x.get('source') and x.get('provenance') for x in catalog))
    observations=json.loads((root/'src/SpeedForge.Core/server-observations.json').read_text(encoding='utf-8-sig'))
    ids={x['id'] for x in catalog}
    ck('Every catalog entry has an observation',ids=={x['ProfileId'] for x in observations},len(observations))
except Exception as e:ck('Catalog JSON',False,str(e))
for f in sources('*.csproj'):
    txt=f.read_text(encoding='utf-8-sig')
    refs=re.findall(r'<ProjectReference Include="([^"]+)"',txt)
    ck('Project paths '+str(f.relative_to(root)),all((f.parent/r.replace('\\','/')).exists() for r in refs),refs)
# These regex checks are indicators only, not a proof of absence of vulnerabilities.
core='\n'.join(p.read_text(encoding='utf-8-sig') for p in (root/'src/SpeedForge.Core').glob('*.cs'))
ck('Process args never built as shell string', 'ArgumentList.Add' in core and 'UseShellExecute = false' in core)
ck('No TLS validation bypass', 'ServerCertificateCustomValidationCallback' not in core and 'DangerousAcceptAnyServerCertificateValidator' not in core)
ck('No automatic binary downloading', 'WebClient' not in core and 'DownloadFile' not in core and 'GetByteArrayAsync' not in core)
ck('No drivers or registry autorun', 'Microsoft.Win32.Registry' not in core and 'CreateService' not in core)
ck('Native EXE hash before launch', 'RequireVerifiedAsync' in core and 'SHA256.HashDataAsync' in core)
ck('DNS IP pin + redirect denial', 'ConnectCallback' in core and 'AllowAutoRedirect = false' in core)
ck('Basic rate/time limits', 'DurationSeconds is < 3 or > 30' in core and 'ParallelStreams > maximumStreams' in core)
failed=[x for x in checks if not x['pass_check']]
report=dict(checked_at_utc=datetime.now(timezone.utc).isoformat(),scope='Static structure only, not build/test execution',source_count=len(files),checks=len(checks),passed=len(checks)-len(failed),failed=len(failed),results=checks)
(root/'STATIC_AUDIT.json').write_text(json.dumps(report,indent=2,ensure_ascii=False),encoding='utf-8')
print(json.dumps({k:v for k,v in report.items() if k!='results'},indent=2))
for c in failed:print('FAIL',c)
raise SystemExit(bool(failed))
