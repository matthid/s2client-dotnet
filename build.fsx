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
open Fake.Api
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.Core.Globbing.Operators
open Fake.DotNet
open Fake.Tools
let projectName = "s2client-dotnet"
let projectSummary = "s2client-dotnet - Starcraft 2 Client API for .NET."
let projectDescription = "s2client-dotnet - Starcraft 2 Client API for .NET - similar to https://github.com/Blizzard/s2client-api"

let authors = ["Matthias Dittrich"]

let release = ReleaseNotes.LoadReleaseNotes "docs/RELEASE_NOTES.md"

let gitRaw = Environment.environVarOrDefault "gitRaw" "https://raw.github.com/matthid"

let gitOwner = "matthid"

// The name of the project on GitHub
let gitName = "s2client-dotnet"

let releaseDir = "./release"
let releaseNugetDir = releaseDir </> "nuget"



let gitHome = "https://github.com/" + gitOwner
let gitRepositoryUrl = gitHome + "/" + gitName

module PaketHelper =
    open Paket
    let getFolder root groupName (p : Paket.PackageResolver.PackageInfo) =
        p.Folder root groupName

let cache = Paket.Constants.UserNuGetPackagesFolder
let deps = Paket.Dependencies.Locate()
let lock = deps.GetLockFile()

Target.Create "Clean" (fun _ ->
    !! "src/*/*/bin"
    |> Shell.CleanDirs

    !! "src/*/*/obj/*.nuspec"
    |> File.deleteAll

    Shell.CleanDirs [releaseNugetDir]
)
let Release_2_0_2 options =
    { options with
        Cli.InstallerOptions = (fun io ->
            { io with
                Branch = "release/2.0.0"
            })
        Cli.Channel = None
        Cli.Version = Cli.Version "2.0.2"
    }

Target.Create "InstallDotNetSdk" (fun _ ->
     Cli.DotnetCliInstall Release_2_0_2
)

Target.Create "DotnetPackage" (fun _ ->
    
    let nugetDir = System.IO.Path.GetFullPath releaseNugetDir

    Environment.setEnvironVar "Version" release.NugetVersion
    Environment.setEnvironVar "Authors" (String.separated ";" authors)
    Environment.setEnvironVar "Description" projectDescription
    Environment.setEnvironVar "PackageReleaseNotes" (release.Notes |> String.toLines)
    Environment.setEnvironVar "PackageTags" "build;fake;f#"
    //Environment.setEnvironVar "PackageIconUrl" "https://raw.githubusercontent.com/fsharp/FAKE/fee4f05a2ee3c646979bf753f3b1f02d927bfde9/help/content/pics/logo.png"
    Environment.setEnvironVar "PackageProjectUrl" gitRepositoryUrl
    Environment.setEnvironVar "PackageLicenseUrl" (gitRepositoryUrl + "/blob/ae301a8af0b596b55b4d1f9a60e1197f66af9437/LICENSE.txt")

    // dotnet pack
    Cli.DotnetPack (fun c ->
        { c with
            Configuration = Cli.Release
            OutputPath = Some nugetDir
        }) "src/s2client-dotnet.sln"

    !! (nugetDir + "/*.nupkg")
    -- (nugetDir + "/s2client-dotnet*.nupkg")
    -- (nugetDir + "/s2client-proto*.nupkg")
    |> Seq.iter (Shell.rm_rf)
)

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
        { conf with
            Arguments = protocArgs
            FileName = protoTools @@ "tools/windows_x64/protoc.exe"})
            (TimeSpan.FromMinutes 5.0)
    if exitCode <> 0 then failwithf "Protoc failed."        
    ()
)


Target.Create "PublishNuget" (fun _ ->
    // uses NugetKey environment variable.
    // Timeout atm
    Paket.Push(fun p ->
        { p with
            DegreeOfParallelism = 2
            WorkingDir = releaseNugetDir })
    //!! (nugetLegacyDir </> "**/*.nupkg")
    //|> Seq.iter nugetPush
)

Target.Create "FastRelease" (fun _ ->

    Git.Staging.StageAll ""
    Git.Commit.Commit "" (sprintf "Bump version to %s" release.NugetVersion)
    let branch = Git.Information.getBranchName ""
    Git.Branches.pushBranch "" "origin" branch

    Git.Branches.tag "" release.NugetVersion
    Git.Branches.pushTag "" "origin" release.NugetVersion

    let token =
        match Environment.environVarOrDefault "github_token" "" with
        | s when not (System.String.IsNullOrWhiteSpace s) -> s
        | _ -> failwith "please set the github_token environment variable to a github personal access token with repro access."

    //let files = 
    //    runtimes @ [ "portable"; "packages" ]
    //    |> List.map (fun n -> sprintf "nuget/dotnetcore/Fake.netcore/fake-dotnetcore-%s.zip" n)
    
    GitHub.CreateClientWithToken token
    |> GitHub.DraftNewRelease gitOwner gitName release.NugetVersion (release.SemVer.PreRelease <> None) release.Notes
    //|> GitHub.UploadFiles files    
    |> GitHub.PublishDraft
    |> Async.RunSynchronously
)

Target.Create "Default" ignore
Target.Create "Release" ignore

open Fake.Core.TargetOperators

"Clean" ==> "Default"
"InstallDotNetSdk" ==> "Default"
"CreateProtobuf" ==> "Default"
"DotnetPackage" ==> "Default"

"Clean"
    ?=> "InstallDotNetSdk"
    ?=> "DotnetPackage"
"Clean"
    ?=> "CreateProtobuf"

"PublishNuget"
    ==> "FastRelease"


// A 'Release' includes a 'Default'
"Default"
    ==> "Release"
// A 'Release' includes a 'FastRelease'
"FastRelease"
    ==> "Release"

Target.RunOrDefault "Default"