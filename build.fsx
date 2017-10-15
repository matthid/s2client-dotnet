#if !DOTNETCORE
#I @"C:\Users\matth\.nuget\packages\FAKE\5.0.0-alpha018\tools"
#r @"FakeLib.dll"
#I @"C:\Users\matth\.nuget\packages\System.Net.Http\4.3.2\lib\net46"
#r "System.Net.Http.dll"
#I @"C:\Users\matth\.nuget\packages\Chessie\0.6.0\lib\net40"
#I @"C:\Users\matth\.nuget\packages\Paket.Core\5.92.100\lib\net45"
#r @"Chessie.dll"
#r @"Paket.Core.dll"
#endif

open System
open System.IO
open System.Reflection
open Fake.Core
open Fake.IO
open Fake.IO.FileSystem.Operators
open Fake.Core.Globbing.Operators
open Fake.DotNet
open Fake.Tools
module PaketHelper =
    open Paket
    let getFolder root groupName (p : Paket.PackageResolver.PackageInfo) =
        p.Folder root groupName

let cache = Paket.Constants.UserNuGetPackagesFolder
let deps = Paket.Dependencies.Locate()
let lock = deps.GetLockFile()

Target.Create "CreateProtobuf" (fun _ ->
    let groupName = Paket.Constants.MainDependencyGroup
    let packageName = Paket.Domain.PackageName "Google.Protobuf.Tools"
    let group = lock.GetGroup(groupName)
    let pack = group.GetPackage(packageName)
    
    let protoTools = PaketHelper.getFolder lock.RootPath groupName pack
    let protoPaths =
        [ protoTools @@ "tools"
          @"external/s2client-proto" ]
        |> List.map (Path.GetFullPath)
    let concatArgs args =
        let allArgs =
            args
            |> String.concat "\" \""
        if String.isNullOrWhiteSpace allArgs then ""
        else sprintf "\"%s\"" allArgs        
    let protoPathArgs =
        protoPaths
        |> Seq.map (sprintf "--proto_path=\"%s\"")
        |> String.concat " "
    let csharpOpts = "--csharp_out=\"src/s2client-proto\""
    let protoArgs =
        !! @"external/s2client-proto/s2clientprotocol/*.proto"
        |> concatArgs
    let protocArgs =
        sprintf "%s %s %s" protoPathArgs csharpOpts protoArgs
    let exitCode =
        Process.ExecProcess (fun conf ->
            conf.Arguments <- protocArgs
            conf.FileName <- protoTools @@ "tools/windows_x64/protoc.exe" )
            (TimeSpan.FromMinutes 5.0)
    if exitCode <> 0 then failwithf "Protoc failed."        
    ()
)

Target.Create "All" ignore

open Fake.Core.TargetOperators

"CreateProtobuf" ==> "All"

Target.RunOrDefault "All"