#!/usr/bin/env python3
"""Create deterministic RID archives and SHA256SUMS."""
import argparse, hashlib, stat, sys, zipfile
from pathlib import Path
FIXED_TIMESTAMP=(2020,1,1,0,0,0)
FORBIDDEN={".afs",".ct",".dat",".emd",".iso",".mrg",".pak",".sav",".tim",".vab",".xa",".pss",".mov"}
SHIPPED_NATIVE={"osx-arm64":{"libAvaloniaNative.dylib","libHarfBuzzSharp.dylib","libSkiaSharp.dylib"}}
class PackageError(RuntimeError): pass
def archive_tree(source: Path, destination: Path, rid: str):
    all_files=sorted(p for p in source.rglob("*") if p.is_file()); expected={"dinorand.exe","DinoRand.Avalonia.exe"} if rid=="win-x64" else {"dinorand","DinoRand.Avalonia"}; actual={p.name for p in all_files if p.parent==source and p.name in expected}
    if actual != expected: raise PackageError(f"{rid}: expected executables {sorted(expected)}, found {sorted(actual)}")
    shipped_native=SHIPPED_NATIVE.get(rid,set()); approved_top=expected|shipped_native|{"LICENSE","LEGAL.md","THIRD-PARTY-NOTICES.md"}
    files=[p for p in all_files if p.relative_to(source).parts[0]=="notices" or (p.parent==source and p.name in approved_top)]
    ignored={p.relative_to(source).as_posix() for p in all_files if p not in files}
    allowed_unshipped={"libHarfBuzzSharp.pdb","libSkiaSharp.pdb"} if rid=="win-x64" else set()
    if ignored != allowed_unshipped: raise PackageError(f"{rid}: unexpected publish outputs {sorted(ignored)}")
    names={p.relative_to(source).as_posix() for p in files}; required={"LICENSE","LEGAL.md","THIRD-PARTY-NOTICES.md","notices/DEPENDENCY-INVENTORY.md","notices/DOTNET-RUNTIME-LICENSE.txt","notices/DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt"}
    if required-names: raise PackageError(f"{rid}: missing {sorted(required-names)}")
    for name in names:
        path=Path(name)
        if path.is_absolute() or ".." in path.parts or path.name==".env" or path.suffix.lower() in FORBIDDEN or any(part in {"bin","obj",".git",".nuget"} for part in path.parts): raise PackageError(f"{rid}: unsafe/unexpected archive path {name}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(destination,"w",zipfile.ZIP_DEFLATED,compresslevel=9) as archive:
        for path in files:
            name=path.relative_to(source).as_posix(); info=zipfile.ZipInfo(name,FIXED_TIMESTAMP); info.external_attr=(stat.S_IFREG | (0o755 if path.name in expected|shipped_native else 0o644))<<16; info.compress_type=zipfile.ZIP_DEFLATED; archive.writestr(info,path.read_bytes(),compresslevel=9)
def write_checksums(asset_dir: Path):
    assets=sorted(p for p in asset_dir.iterdir() if p.is_file() and p.name!="SHA256SUMS")
    if not assets: raise PackageError("no release assets")
    out=asset_dir/"SHA256SUMS"; out.write_text("\n".join(f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.name}" for p in assets)+"\n",encoding="ascii",newline="\n"); return out
def main(argv=None):
    parser=argparse.ArgumentParser(); sub=parser.add_subparsers(dest="command",required=True); arc=sub.add_parser("archive"); arc.add_argument("--source",type=Path,required=True); arc.add_argument("--output",type=Path,required=True); arc.add_argument("--rid",required=True); sums=sub.add_parser("checksums"); sums.add_argument("--asset-dir",type=Path,required=True); args=parser.parse_args(argv)
    try:
        if args.command=="archive": archive_tree(args.source.resolve(),args.output.resolve(),args.rid); print(args.output)
        else: print(write_checksums(args.asset_dir.resolve()))
    except PackageError as error: print(f"release packaging failed: {error}",file=sys.stderr); return 1
    return 0
if __name__=="__main__": raise SystemExit(main())
