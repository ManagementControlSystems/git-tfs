using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using NDesk.Options;
using Sep.Git.Tfs.Core;
using StructureMap;

namespace Sep.Git.Tfs.Commands
{
    [Pluggable("subtree")]
    [Description("subtree [add|pull|split] [options] [remote | ( [tfs-url] [repository-path] )]")]
    [RequiresValidGitRepository]
    public class Subtree : GitTfsCommand
    {
        
        private readonly TextWriter _stdout;
        private readonly Fetch _fetch;
        private readonly QuickFetch _quickFetch;
        private readonly Globals _globals;
        private readonly RemoteOptions _remoteOptions;

        private string Prefix;
        private bool Squash = false;

        public OptionSet OptionSet
        {
            get
            {
                return new OptionSet
                {
                    { "p|prefix=",
                        v => Prefix = v},
                    { "squash",
                        v => Squash = v != null},
                    //                    { "r|revision=",
                    //                        v => RevisionToFetch = Convert.ToInt32(v) },
                }
                .Merge(_fetch.OptionSet);
            }
        }

        public Subtree(TextWriter stdout, Fetch fetch, QuickFetch quickFetch, Globals globals, RemoteOptions remoteOptions)
        {
            this._stdout = stdout;
            this._fetch = fetch;
            this._quickFetch = quickFetch;
            this._globals = globals;
            this._remoteOptions = remoteOptions;
        }

        public int Run(IList<string> args)
        {
            string command = args.FirstOrDefault() ?? "";
            _stdout.WriteLine("executing subtree " + command);

            if (string.IsNullOrEmpty(Prefix))
            {
                _stdout.WriteLine("Prefix must be specified, use -p or -prefix");
                return GitTfsExitCodes.InvalidArguments;
            }

            switch (command.ToLower())
            {
                case "add":
                    return DoAdd(args.ElementAtOrDefault(1) ?? "", args.ElementAtOrDefault(2) ?? "");
                    break;

                case "pull":
                    return DoPull(args.ElementAtOrDefault(1));

                case "split":
                    return DoSplit();

                default:
                    _stdout.WriteLine("Expected one of [add, pull, split]");
                    return GitTfsExitCodes.InvalidArguments;
                    break;
            }
        }

        public int DoAdd(string tfsUrl, string tfsRepositoryPath)
        {
            if (File.Exists(Prefix) || Directory.Exists(Prefix))
            {
                _stdout.WriteLine(string.Format("Directory {0} already exists", Prefix));
                return GitTfsExitCodes.InvalidArguments;
            }


            var fetch = Squash ? this._quickFetch : this._fetch;

            IGitTfsRemote owner = null;
            string ownerId = _globals.RemoteId;
            if (!string.IsNullOrEmpty(ownerId))
            {
                //check for the specified remote
                owner = _globals.Repository.HasRemote(_globals.RemoteId) ? _globals.Repository.ReadTfsRemote(_globals.RemoteId) : null;
                if (owner != null && !string.IsNullOrEmpty(owner.TfsRepositoryPath))
                {
                    owner = null;
                    ownerId = null;
                }
            }
            
            if(string.IsNullOrEmpty(ownerId))
            {
                //check for any remote that has no TfsRepositoryPath
                owner = _globals.Repository.ReadAllTfsRemotes().FirstOrDefault(x => string.IsNullOrEmpty(x.TfsRepositoryPath) && !x.IsSubtree);
            }

            if (owner == null)
            {
                owner = _globals.Repository.CreateTfsRemote(new RemoteInfo
                {
                    Id = ownerId ?? GitTfsConstants.DefaultRepositoryId,
                    Url = tfsUrl,
                    Repository = null,
                    RemoteOptions = _remoteOptions
                });
                _stdout.WriteLine("-> new owning remote " + owner.Id);
            }
            else
            {
                ownerId = owner.Id;
                _stdout.WriteLine("Attaching subtree to owning remote " + owner.Id);
            }
            

            //create a remote for the new subtree
            string remoteId = string.Format(GitTfsConstants.RemoteSubtreeFormat, owner.Id, Prefix);
            IGitTfsRemote remote = _globals.Repository.HasRemote(remoteId) ? 
                _globals.Repository.ReadTfsRemote(remoteId) :
                _globals.Repository.CreateTfsRemote(new RemoteInfo
                 {
                     Id = remoteId,
                     Url = tfsUrl,
                     Repository = tfsRepositoryPath,
                     RemoteOptions = _remoteOptions,
                 });
            
            _stdout.WriteLine("-> new remote " + remote.Id);
            
            int result = fetch.Run(remote.Id);
            
            if (result == GitTfsExitCodes.OK)
            {
                var p = Prefix.Replace(" ", "\\ ");

                long latest = Math.Max(owner.MaxChangesetId, remote.MaxChangesetId);
                string msg = string.Format(GitTfsConstants.TfsCommitInfoFormat, owner.TfsUrl, owner.TfsRepositoryPath, latest);
                msg = string.Format(@"Add '{0}/' from commit '{1}'

{2}", Prefix, remote.MaxCommitHash, msg);

                _globals.Repository.CommandNoisy(
                    "subtree", "add",
                    "--prefix=" + p,
                    string.Format("-m {0}", msg),
                    remote.RemoteRef);

                //update the owner remote to point at the commit where the newly created subtree was merged.
                var commit = _globals.Repository.GetCurrentCommit();
                owner.UpdateTfsHead(commit, latest);

                result = GitTfsExitCodes.OK;
            }
            

            return result;
        }
    
        public int DoPull(string remoteId)
        {
            ValidatePrefix();

            remoteId = remoteId ?? string.Format(GitTfsConstants.RemoteSubtreeFormat, _globals.RemoteId ?? GitTfsConstants.DefaultRepositoryId, Prefix);
            IGitTfsRemote remote = _globals.Repository.ReadTfsRemote(remoteId);

            int result = this._fetch.Run(remote.Id);
            if (result == GitTfsExitCodes.OK)
            {
                var p = Prefix.Replace(" ", "\\ ");
                _globals.Repository.CommandNoisy("subtree", "merge", "--prefix=" + p, remote.RemoteRef);
                result = GitTfsExitCodes.OK;
            }

            return result;
        }

        public int DoSplit()
        {
            ValidatePrefix();

            var p = Prefix.Replace(" ", "\\ ");
            _globals.Repository.CommandNoisy("subtree", "split", "--prefix=" + p, "-b", p);
            _globals.Repository.CommandNoisy("checkout", p);

            //update subtree refs if needed
            var owners = _globals.Repository.GetLastParentTfsCommits("HEAD").Where(x => !x.Remote.IsSubtree && x.Remote.TfsRepositoryPath == null).ToList();
            foreach (var subtree in _globals.Repository.ReadAllTfsRemotes().Where(x => x.IsSubtree && string.Equals(x.Prefix, Prefix)))
            {
                var updateTo = owners.FirstOrDefault(x => string.Equals(x.Remote.Id, subtree.OwningRemoteId));
                if (updateTo != null && updateTo.ChangesetId > subtree.MaxChangesetId)
                {
                    subtree.UpdateTfsHead(updateTo.GitCommit, updateTo.ChangesetId);
                }
            }

            return GitTfsExitCodes.OK;
        }

        private void ValidatePrefix()
        {
            if (!Directory.Exists(Prefix))
            {
                throw new GitTfsException(string.Format("Directory {0} does not exist", Prefix))
                    .WithRecommendation("Add the subtree using 'git tfs subtree add -p=<prefix> [tfs-server] [tfs-repository]'");
            }
        }
    }
}
