using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GitViz.Logic.Annotations;
using System;
using System.Text.RegularExpressions;

namespace GitViz.Logic
{
    public class ViewModel : INotifyPropertyChanged
    {
        string _repositoryPath = "";
        CommitGraph _graph = new CommitGraph();
        RepositoryWatcher _watcher;

        readonly LogParser _parser = new LogParser();

        public string WindowTitle
        {
            get
            {
                return "Readify GitViz (Alpha)"
                    + (string.IsNullOrWhiteSpace(_repositoryPath)
                        ? string.Empty
                        : " - " + Path.GetFileName(_repositoryPath));
            }
        }

        GitCommandExecutor commandExecutor;
        LogRetriever logRetriever;

        public bool IsNewRepository { get; set; }
        public ViewModel()
        {
            _numOfCommitsToShow = 20;
        }

        public string RepositoryPath
        {
            get { return _repositoryPath; }
            set
            {
                _repositoryPath = value;
                if (IsValidGitRepository(_repositoryPath))
                {
                    OnPropertyChanged("WindowTitle");
                     commandExecutor = new GitCommandExecutor(_repositoryPath);
                     logRetriever = new LogRetriever(commandExecutor, _parser);

                    RefreshGraph(logRetriever);

                    IsNewRepository = true;

                    _watcher = new RepositoryWatcher(_repositoryPath, IsBareGitRepository(_repositoryPath));
                    _watcher.ChangeDetected += (sender, args) => RefreshGraph(logRetriever);
                }
                else
                {
                    Graph = new CommitGraph();
                    if (_watcher != null)
                    {
                        _watcher.Dispose();
                        _watcher = null;
                    }
                }
            }
        }

        void RefreshGraph(LogRetriever logRetriever)
        {
            var commits = logRetriever.GetRecentCommits(NumOfCommitsToShow).ToArray();
            var activeRefName = logRetriever.GetActiveReferenceName();

            var reachableCommitHashes = commits.Select(c => c.Hash).ToArray();
            var unreachableHashes = logRetriever.GetRecentUnreachableCommitHashes();
            IEnumerable<Commit> unreachableCommits = null;
            if (VisualizeUnreachable)
            {
                unreachableCommits = logRetriever
                    .GetSpecificCommits(unreachableHashes)
                    .Where(c => !reachableCommitHashes.Contains(c.Hash))
                    .ToArray();
            }

            Graph = GenerateGraphFromCommits(commits, activeRefName, unreachableCommits);
        }

        CommitGraph GenerateGraphFromCommits(IEnumerable<Commit> commits, string activeRefName, IEnumerable<Commit> unreachableCommits)
        {
            commits = commits.ToList();

            int i = 1;
            AssignFriendlyName(commits, ref i);
            AssignFriendlyName(unreachableCommits, ref i);

            var graph = new CommitGraph();
            List<Vertex> commitVertices;
            if (unreachableCommits != null)
            {
                commitVertices = commits.Select(c => new Vertex(c))
                .Union(unreachableCommits.Select(c => new Vertex(c) { Orphan = true }))
                .ToList();
            }
            else
            {
            commitVertices = commits.Select(c => new Vertex(c))
                .ToList();
            }

            // Add all the vertices
            var headVertex = new Vertex(new Reference
            {
               Name = Reference.HEAD,
               IsHead = true,
            });

            foreach (var commitVertex in commitVertices)
            {
                graph.AddVertex(commitVertex);

                if (commitVertex.Commit.Refs == null) continue;
                foreach (var refName in commitVertex.Commit.Refs)
                {
                    var name = refName.StartsWith(Reference.HeadCheckouted) ? refName.Substring(8) : refName;
                    var refVertex = new Vertex(new Reference
                    {
                        Name = name,
                        IsActive = name == activeRefName,
                        IsHead = name == Reference.HEAD,
                    });
                    graph.AddVertex(refVertex);

                    if (refVertex.Reference.IsActive)
                    {
                        graph.AddVertex(headVertex);
                        graph.AddEdge(new CommitEdge(headVertex, refVertex));
                    }

                    graph.AddEdge(new CommitEdge(refVertex, commitVertex));
                }
            }

            // Add all the edges
            foreach (var commitVertex in commitVertices.Where(c => c.Commit.ParentHashes != null))
            {
                foreach (var parentHash in commitVertex.Commit.ParentHashes)
                {
                    var parentVertex = commitVertices.SingleOrDefault(c => c.Commit.Hash == parentHash);
                    if (parentVertex != null) graph.AddEdge(new CommitEdge(commitVertex, parentVertex));
                }
            }

            return graph;
        }

        private void AssignFriendlyName(IEnumerable<Commit> commits, ref int offset)
        {
            if (commits == null)
                return;
            foreach (var commit in commits.Reverse())
            {
                commit.FriendlyName = "C" + offset;
                offset++;
            }
        }

        public CommitGraph Graph
        {
            get { return _graph; }
            set
            {
                _graph = value;
                OnPropertyChanged("Graph");
            }
        }

        public Boolean VisualizeUnreachable
        {
            get { return _visualizeUnreachable; }
            set
            {
                _visualizeUnreachable = value;
                if (logRetriever != null) RefreshGraph(logRetriever); //TODO: Refactor.
                OnPropertyChanged("VisualizeUnreachable");
            }
        }
        private Boolean _visualizeUnreachable;

        public Boolean VisualizeComments
        {
            get { return _visualizeComments; }
            set
            {
                _visualizeComments = value;
                if (logRetriever != null) RefreshGraph(logRetriever); //TODO: Refactor.
                OnPropertyChanged("VisualizeComments");
            }
        }
        private Boolean _visualizeComments;

        public Boolean FriendlyNaming
        {
            get { return _friendlyNaming; }
            set
            {
                _friendlyNaming = value;
                if (logRetriever != null) RefreshGraph(logRetriever); //TODO: Refactor.
                OnPropertyChanged("FriendlyNaming");
            }
        }
        private Boolean _friendlyNaming;

        public Int32 NumOfCommitsToShow
        {
            get { return _numOfCommitsToShow; }
            set
            {
                _numOfCommitsToShow = value;
                if (logRetriever != null) RefreshGraph(logRetriever); //TODO: Refactor.
                OnPropertyChanged("NumOfCommitsToShow");
            }
        }
        private Int32 _numOfCommitsToShow;

        static bool IsValidGitRepository(string path)
        {
            return !string.IsNullOrEmpty(path)
                && Directory.Exists(path)
                && (Directory.Exists(Path.Combine(path, ".git")) ||
                 IsBareGitRepository(path));
        }

        static Boolean IsBareGitRepository(String path)
        {
            String configFileForBareRepository = Path.Combine(path, "config");
            return File.Exists(configFileForBareRepository) &&
                  Regex.IsMatch(File.ReadAllText(configFileForBareRepository), @"bare\s*=\s*true", RegexOptions.IgnoreCase);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
