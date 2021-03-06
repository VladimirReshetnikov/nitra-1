﻿using Nitra.Declarations;
using Nitra.ProjectSystem;
using Nitra.VisualStudio;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using JetBrains.ActionManagement;
using JetBrains.Annotations;
using JetBrains.Application;
using JetBrains.Application.changes;
using JetBrains.Application.CommandProcessing;
using JetBrains.Application.DataContext;
using JetBrains.DataFlow;
using JetBrains.DocumentManagers;
using JetBrains.DocumentManagers.impl;
using JetBrains.DocumentModel;
using JetBrains.Metadata.Reader.API;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.Model2.Assemblies.Interfaces;
using JetBrains.ReSharper.Feature.Services.LiveTemplates.LiveTemplates;
using JetBrains.ReSharper.Feature.Services.Lookup;
using JetBrains.ReSharper.Feature.Services.Navigation.ContextNavigation;
using JetBrains.ReSharper.Feature.Services.Util;
using JetBrains.ReSharper.Features.Intellisense.CodeCompletion.CSharp.Rules.SourceTemplates;
using JetBrains.ReSharper.Features.Navigation.Features.FindUsages;
using JetBrains.ReSharper.Features.Navigation.Features.GoToDeclaration;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.TextControl;
using JetBrains.TextControl.Util;
using JetBrains.UI.ActionsRevised;
using JetBrains.UI.ActionSystem.Text;
using JetBrains.UI.PopupMenu;
using JetBrains.Util;

namespace XXNamespaceXX.ProjectSystem
{
  public partial class XXLanguageXXSolution : Solution, INitraSolutionService
  {
    public bool IsOpened { get; private set; }

    private ISolution _solution;
    private readonly Dictionary<IProject, XXLanguageXXProject> _projectsMap = new Dictionary<IProject, XXLanguageXXProject>();
    private readonly Dictionary<string, Action<File>> _fileOpenNotifyRequest = new Dictionary<string, Action<File>>(StringComparer.OrdinalIgnoreCase);
    private IActionManager _actionManager;
    private JetPopupMenus _jetPopupMenus;

    public DocumentManager DocumentManager { get; private set; }

    public XXLanguageXXSolution()
    {
    }

    public void Open(Lifetime lifetime, IShellLocks shellLocks, ChangeManager changeManager, ISolution solution, DocumentManager documentManager, IActionManager actionManager, ICommandProcessor commandProcessor, TextControlChangeUnitFactory changeUnitFactory, JetPopupMenus jetPopupMenus)
    {
      Debug.Assert(!IsOpened);

      _solution = solution;
      DocumentManager = documentManager;
      _jetPopupMenus = jetPopupMenus;
      changeManager.Changed2.Advise(lifetime, Handler);
      lifetime.AddAction(Close);
      var expandAction = actionManager.Defs.TryGetActionDefById(GotoDeclarationAction.ACTION_ID);
      if (expandAction != null)
      {
        var postfixHandler = new GotoDeclarationHandler(lifetime, shellLocks, commandProcessor, changeUnitFactory, this);

        lifetime.AddBracket(
          FOpening: () => actionManager.Handlers.AddHandler(expandAction, postfixHandler),
          FClosing: () => actionManager.Handlers.RemoveHandler(expandAction, postfixHandler));
      }
      
      var findUsagesAction = actionManager.Defs.GetActionDef<FindUsagesAction>();
      var findUsagesHandler = new FindUsagesHandler(lifetime, shellLocks, commandProcessor, changeUnitFactory, this);

      lifetime.AddBracket(
        FOpening: () => actionManager.Handlers.AddHandler(findUsagesAction, findUsagesHandler),
        FClosing: () => actionManager.Handlers.RemoveHandler(findUsagesAction, findUsagesHandler));
    }

    private void Close()
    {
      IsOpened = false;
      foreach (var project in _projectsMap.Values)
        project.Dispose();
      _projectsMap.Clear();
      _solution = null;
      _fileOpenNotifyRequest.Clear();
      DocumentManager = null;
    }

    public override IEnumerable<Project> Projects { get { return _projectsMap.Values; } }

    private void Handler(ChangeEventArgs changeEventArgs)
    {
      var projectModelChange = changeEventArgs.ChangeMap.GetChange<ProjectModelChange>(_solution);
      if (projectModelChange != null)
      {
        if (projectModelChange.ContainsChangeType(ProjectModelChangeType.PROJECT_MODEL_CACHES_READY))
        {
          IsOpened = true;

          var values = _projectsMap.Values.ToArray();


          foreach (var project in values)
          {
            project.Data = null;
            project.UpdateProperties();
          }
        }

        projectModelChange.Accept(new RecursiveProjectModelChangeDeltaVisitor(FWithDelta, FWithItemDelta));
      }

      {
        var documentChange = changeEventArgs.ChangeMap.GetChange<ProjectFileDocumentChange>(DocumentManager.ChangeProvider);
        if (documentChange != null)
          if (OnFileChanged(documentChange.ProjectFile, documentChange))
            return;
      }

      {
        var documentChange = changeEventArgs.ChangeMap.GetChange<DocumentChange>(DocumentManager.ChangeProvider);
        if (documentChange != null)
          if (OnFileChanged(DocumentManager.GetProjectFile(documentChange.Document), documentChange))
            return;
      }
    }

    private bool OnFileChanged(IProjectFile projectFile, DocumentChange documentChange)
    {
      var project = projectFile.GetProject();
      if (project != null)
      {
        XXLanguageXXProject nitraProject;
        if (_projectsMap.TryGetValue(project, out nitraProject))
        {
          var nitraFile = nitraProject.TryGetFile(projectFile);
          if (nitraFile != null)
          {
            nitraFile.OnFileChanged(documentChange);
            return true;
          }
        }
      }

      return false;
    }
    
    private void FWithDelta(ProjectModelChange obj)
    {
      var reference = obj as ProjectReferenceTargetChange;
      if (reference != null)
      {
        var assembly = reference.NewReferenceTarget as IAssembly;
        if (assembly != null)
        {
          var projectElement = reference.ProjectModelElement as IProjectElement;
          if (projectElement == null)
            return;

          var project = GetProject(projectElement.GetProject());

          project.Data = null;

          using (var loader = new MetadataLoader())
          {
            var path = assembly.GetFiles().First().Location;
            project._libs.Add(new FileLibReference(path.ToString()));
            var metadataAssembly = loader.LoadFrom(path, x => true);
            foreach (var a in metadataAssembly.CustomAttributesTypeNames)
              if (a.FullName.EqualTo("Nitra.GrammarsAttribute"))
                project.TryAddNitraExtensionAssemblyReference(path);
          }

          if (IsOpened)
            project.UpdateProperties();
        }
      }
    }

    private void FWithItemDelta(ProjectItemChange obj)
    {
      var item = obj.ProjectItem;

      var file = item as IProjectFile;
      if (file != null && item.ParentFolder != null && file.LanguageType.Is<XXLanguageXXFileType>())
      {
        if (obj.IsRemoved || obj.IsMovedOut)
        {
          var project = GetProject(obj.OldParentFolder.GetProject());
          project.TryRemoveFile(file);
        }
        else if (obj.IsAdded || obj.IsMovedIn)
        {
          var project = GetProject(file.GetProject());
          var sourceFile = file.ToSourceFile();
          if (sourceFile == null)
            return;

          var nitraFile = project.TryAddFile(file);

          if (IsOpened)
            project.UpdateProperties();

          Action<File> oldHandler;
          if (_fileOpenNotifyRequest.TryGetValue(nitraFile.FullName, out oldHandler))
            oldHandler(nitraFile);
        }
      }
    }

    public XXLanguageXXProject GetProject(IProject project)
    {
      XXLanguageXXProject result;
      if (_projectsMap.TryGetValue(project, out result))
        return result;

      result = new XXLanguageXXProject(this, project);
      
      _projectsMap.Add(project, result);

      return result;
    }

    /// <summary>
    /// INitraSolutionService.NotifiWhenFileIsOpened implementation.
    /// </summary>
    public void NotifiWhenFileIsOpened(string filePath, Action<File> handler)
    {
      if (IsOpened)
      {
        foreach (var project in _projectsMap.Values)
        {
          var file = project.TryGetFile(filePath);
          if (file == null)
            continue;

          handler(file);
          return;
        }
      }

      Action<File> oldHandler;
      if (_fileOpenNotifyRequest.TryGetValue(filePath, out oldHandler))
        _fileOpenNotifyRequest[filePath] = oldHandler + handler;
      else
        _fileOpenNotifyRequest.Add(filePath, handler);
    }

    public bool IsNitraFile(IPsiSourceFile sourceFile)
    {
      return GetNitraFile(sourceFile) != null;
    }

    [CanBeNull]
    [ContractAnnotation("null <= null")]
    public XXLanguageXXFile GetNitraFile(IDocument doc)
    {
      if (doc == null)
        return null;

      var projectFile = DocumentManager.TryGetProjectFile(doc);
      if (projectFile == null)
        return null;

      return GetNitraFile(projectFile);
    }

    public XXLanguageXXFile GetNitraFile(IProjectFile projectFile)
    {
      var project = projectFile.GetProject();
      if (project == null)
        return null;

      XXLanguageXXProject nitraLangProject;

      if (!_projectsMap.TryGetValue(project, out nitraLangProject))
        return null;

      return nitraLangProject.TryGetFile(projectFile);
    }

    [CanBeNull]
    [ContractAnnotation("null <= null")]
    public XXLanguageXXFile GetNitraFile(IPsiSourceFile sourceFile)
    {
      if (sourceFile == null)
        return null;

      var projectFile = sourceFile.ToProjectFile();
      if (projectFile == null)
        return null;

      return GetNitraFile(projectFile);
    }
  }
}
