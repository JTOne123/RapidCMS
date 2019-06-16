﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using RapidCMS.Common.Authorization;
using RapidCMS.Common.Data;
using RapidCMS.Common.Enums;
using RapidCMS.Common.Exceptions;
using RapidCMS.Common.Extensions;
using RapidCMS.Common.Validation;
using RapidCMS.Common.Helpers;
using RapidCMS.Common.Models;
using RapidCMS.Common.Models.Commands;
using RapidCMS.Common.Models.UI;
using RapidCMS.Common.Validation;

namespace RapidCMS.Common.Services
{
    internal class CollectionService : ICollectionService
    {
        private readonly Root _root;
        private readonly IUIService _uiService;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IAuthorizationService _authorizationService;
        private readonly IServiceProvider _serviceProvider;

        public CollectionService(
            Root root,
            IUIService uiService,
            IHttpContextAccessor httpContextAccessor,
            IAuthorizationService authorizationService,
            IServiceProvider serviceProvider)
        {
            _root = root;
            _uiService = uiService;
            _httpContextAccessor = httpContextAccessor;
            _authorizationService = authorizationService;
            _serviceProvider = serviceProvider;
        }

        private UsageType MapActionToUsageType(string action)
        {
            return action switch
            {
                Constants.Edit => UsageType.Edit,
                Constants.New => UsageType.New,
                Constants.View => UsageType.View,
                Constants.List => UsageType.List,
                _ => (UsageType)0
            };
        }

        private OperationAuthorizationRequirement MapActionToOperation(string action)
        {
            return action switch
            {
                Constants.Edit => Operations.Update,
                Constants.New => Operations.Create,
                Constants.View => Operations.View,
                Constants.List => Operations.List,
                _ => throw new InvalidOperationException()
            };
        }

        public async Task<NodeUI> GetNodeEditorAsync(string action, string alias, string variantAlias, string? parentId, string? id)
        {
            var usageType = UsageType.Node | MapActionToUsageType(action);

            var collection = _root.GetCollection(alias);

            var config = usageType.HasFlag(UsageType.View) ? collection.NodeView : collection.NodeEditor;
            if (config == null)
            {
                throw new InvalidOperationException($"Failed to get UI configuration from collection {alias} for action {action}");
            }

            var entityVariant = collection.GetEntityVariant(variantAlias);

            var entity = usageType switch
            {
                UsageType.Node | UsageType.View => await collection.Repository._GetByIdAsync(id, parentId),
                UsageType.Node | UsageType.Edit => await collection.Repository._GetByIdAsync(id, parentId),
                UsageType.Node | UsageType.New => await collection.Repository._NewAsync(parentId, entityVariant.Type),
                _ => null
            };

            if (entity == null)
            {
                throw new Exception("Failed to get entity for given id(s)");
            }

            if (!usageType.HasFlag(UsageType.New))
            {
                entityVariant = collection.GetEntityVariant(entity);
            }

            var authorizationChallenge = await _authorizationService.AuthorizeAsync(
                _httpContextAccessor.HttpContext.User,
                entity,
                MapActionToOperation(action));

            if (!authorizationChallenge.Succeeded)
            {
                throw new UnauthorizedAccessException();
            }

            var viewContext = new ViewContext(usageType, entityVariant, entity);
            var editContext = new EditContext(entity, usageType, _serviceProvider);

            var node = await _uiService.GenerateNodeUIAsync(viewContext, editContext, config);
            return node;
        }

        public async Task<ViewCommand> ProcessNodeEditorActionAsync(string collectionAlias, string variantAlias, string? parentId, string? id, EditContext editContext, string actionId, object? customData)
        {
            var collection = _root.GetCollection(collectionAlias);

            var entityVariant = collection.GetEntityVariant(variantAlias);

            var nodeEditor = collection.NodeEditor;
            var button = nodeEditor?.Buttons?.GetAllButtons().FirstOrDefault(x => x.ButtonId == actionId);
            if (button == null)
            {
                throw new Exception($"Cannot determine which button triggered action for collection {collectionAlias}");
            }

            var buttonCrudType = button.GetCrudType();

            var updatedEntity = editContext.Entity;

            var authorizationChallenge = await _authorizationService.AuthorizeAsync(
                _httpContextAccessor.HttpContext.User,
                updatedEntity,
                Operations.GetOperationForCrudType(buttonCrudType));

            if (!authorizationChallenge.Succeeded)
            {
                throw new UnauthorizedAccessException();
            }

            if (!editContext.IsValid())
            {
                throw new InvalidEntityException();
            }

            // TODO: fix this
            RelationContainer relations = null;
            // var relations = new RelationContainer(node.Sections.SelectMany(x => x.GetRelations()));

            // TODO: what to do with this action
            if (button is CustomButton customButton)
            {
                await customButton.HandleActionAsync(parentId, id, customData);
            }

            switch (buttonCrudType)
            {
                case CrudType.View:
                    return new NavigateCommand { Uri = UriHelper.Node(Constants.View, collectionAlias, entityVariant, parentId, id) };

                case CrudType.Read:
                    return new NavigateCommand { Uri = UriHelper.Node(Constants.Edit, collectionAlias, entityVariant, parentId, id) };

                case CrudType.Update:
                    await collection.Repository._UpdateAsync(id, parentId, updatedEntity, relations);
                    return new ReloadCommand();

                case CrudType.Insert:
                    var entity = await collection.Repository._InsertAsync(parentId, updatedEntity, relations);
                    return new NavigateCommand { Uri = UriHelper.Node(Constants.Edit, collectionAlias, entityVariant, parentId, entity.Id) };

                case CrudType.Delete:
                    await collection.Repository._DeleteAsync(id, parentId);
                    return new NavigateCommand { Uri = UriHelper.Collection(Constants.List, collectionAlias, parentId) };

                case CrudType.None:
                    return new NoOperationCommand();

                case CrudType.Refresh:
                    return new ReloadCommand();

                case CrudType.Create:
                default:
                    throw new InvalidOperationException();
            }
        }

        public async Task<ListUI> GetCollectionListViewAsync(string action, string alias, string? variantAlias, string? parentId)
        {
            var listUsageType = UsageType.List | MapActionToUsageType(action);

            var collection = _root.GetCollection(alias);

            var subEntityVariant = collection.GetEntityVariant(variantAlias);
            var newEntity = await collection.Repository._NewAsync(parentId, subEntityVariant.Type);

            var authorizationChallenge = await _authorizationService.AuthorizeAsync(
                _httpContextAccessor.HttpContext.User,
                newEntity,
                MapActionToOperation(action));

            if (!authorizationChallenge.Succeeded)
            {
                throw new UnauthorizedAccessException();
            }

            var existingEntities = await collection.Repository._GetAllAsObjectsAsync(parentId);

            IEnumerable<EditContext> entities;

            if (listUsageType.HasFlag(UsageType.New))
            {
                entities = new[] {
                    new EditContext(newEntity, UsageType.Node | MapActionToUsageType(Constants.New), _serviceProvider) }
                    .Concat(existingEntities.Select(ent => new EditContext(ent, UsageType.Node | MapActionToUsageType(Constants.Edit), _serviceProvider)));
            }
            else
            {
                entities = existingEntities.Select(ent => new EditContext(ent, UsageType.Node | MapActionToUsageType(Constants.Edit), _serviceProvider));
            }

            var listViewContext = new ViewContext(listUsageType, collection.EntityVariant, newEntity);
            var rootEditContext = new EditContext(listUsageType);

            if (listUsageType == UsageType.List)
            {
                if (collection.ListView == null)
                {
                    throw new InvalidOperationException($"Failed to get UI configuration from collection {alias} for action {action}");
                }

                var editor = await _uiService.GenerateListUIAsync(
                    listViewContext,
                    rootEditContext,
                    entities,
                    (context) => new ViewContext(context.UsageType, collection.GetEntityVariant(context.Entity), context.Entity),
                    collection.ListView);

                return editor;
            }
            else if (listUsageType.HasFlag(UsageType.Edit) || listUsageType.HasFlag(UsageType.New))
            {
                if (collection.ListEditor == null)
                {
                    throw new InvalidOperationException($"Failed to get UI configuration from collection {alias} for action {action}");
                }

                var editor = await _uiService.GenerateListUIAsync(
                    listViewContext,
                    rootEditContext,
                    entities,
                    (context) => new ViewContext(context.UsageType, collection.GetEntityVariant(context.Entity), context.Entity),
                    collection.ListEditor);

                return editor;
            }
            else
            {
                throw new NotImplementedException($"Failed to process {action} for collection {alias}");
            }
        }

        public async Task<ViewCommand> ProcessListActionAsync(string action, string collectionAlias, string? parentId, string actionId, object? customData)
        {
            var collection = _root.GetCollection(collectionAlias);
            var usageType = MapActionToUsageType(action);

            var buttons = usageType.HasFlag(UsageType.List)
                ? collection.ListView?.Buttons
                : collection.ListEditor?.Buttons;
            var button = buttons?.GetAllButtons().FirstOrDefault(x => x.ButtonId == actionId);

            if (button == null)
            {
                throw new Exception($"Cannot determine which button triggered action for collection {collectionAlias}");
            }

            var entity = await collection.Repository._NewAsync(parentId, collection.EntityVariant.Type);

            var buttonCrudType = button.GetCrudType();

            var authorizationChallenge = await _authorizationService.AuthorizeAsync(
               _httpContextAccessor.HttpContext.User,
               entity,
               Operations.GetOperationForCrudType(buttonCrudType));

            if (!authorizationChallenge.Succeeded)
            {
                throw new UnauthorizedAccessException();
            }

            // TODO: what to do with this action
            if (button is CustomButton customButton)
            {
                await customButton.HandleActionAsync(parentId, null, customData);
            }

            switch (buttonCrudType)
            {
                case CrudType.Create:
                    if (!(button.Metadata is EntityVariant entityVariant))
                    {
                        throw new InvalidOperationException();
                    }

                    if (usageType.HasFlag(UsageType.List))
                    {
                        return new NavigateCommand { Uri = UriHelper.Node(Constants.New, collectionAlias, entityVariant, parentId, null) };
                    }
                    else
                    {
                        return new UpdateParameterCommand
                        {
                            Action = Constants.New,
                            CollectionAlias = collectionAlias,
                            VariantAlias = entityVariant.Alias,
                            ParentId = parentId,
                            Id = null
                        };
                    }

                case CrudType.None:
                    return new NoOperationCommand();

                case CrudType.Refresh:
                    return new ReloadCommand();

                case CrudType.View:
                case CrudType.Read:
                case CrudType.Insert:
                case CrudType.Update:
                case CrudType.Delete:
                default:
                    throw new InvalidOperationException();
            }
        }

        public async Task<ViewCommand> ProcessListActionAsync(string action, string collectionAlias, string? parentId, string id, EditContext editContext, string actionId, object? customData)
        {
            var collection = _root.GetCollection(collectionAlias);
            var usageType = MapActionToUsageType(action);

            var buttons = usageType.HasFlag(UsageType.List)
                ? collection.ListView?.ViewPane?.Buttons
                : collection.ListEditor?.EditorPanes?.SelectMany(pane => pane.Buttons);
            var button = buttons?.GetAllButtons().FirstOrDefault(x => x.ButtonId == actionId);

            if (button == null)
            {
                throw new Exception($"Cannot determine which button triggered action for collection {collectionAlias}");
            }

            var buttonCrudType = button.GetCrudType();

            var updatedEntity = editContext.Entity;

            var authorizationChallenge = await _authorizationService.AuthorizeAsync(
                _httpContextAccessor.HttpContext.User,
                updatedEntity,
                Operations.GetOperationForCrudType(buttonCrudType));

            if (!authorizationChallenge.Succeeded)
            {
                throw new UnauthorizedAccessException();
            }

            // since the id is known, get the entity variant from the entity
            var entityVariant = collection.GetEntityVariant(updatedEntity);

            // TODO: fix this
            RelationContainer relations = null;
            // var relations = new RelationContainer(node.Sections.SelectMany(x => x.GetRelations()));

            // TODO: what to do with this action
            if (button is CustomButton customButton)
            {
                await customButton.HandleActionAsync(parentId, id, customData);
            }

            switch (buttonCrudType)
            {
                case CrudType.View:
                    return new NavigateCommand { Uri = UriHelper.Node(Constants.View, collectionAlias, entityVariant, parentId, id) };

                case CrudType.Read:
                    return new NavigateCommand { Uri = UriHelper.Node(Constants.Edit, collectionAlias, entityVariant, parentId, id) };

                case CrudType.Update:
                    await collection.Repository._UpdateAsync(id, parentId, updatedEntity, relations);
                    return new ReloadCommand();

                case CrudType.Insert:
                    updatedEntity = await collection.Repository._InsertAsync(parentId, updatedEntity, relations);
                    return new UpdateParameterCommand
                    {
                        Action = Constants.New,
                        CollectionAlias = collectionAlias,
                        VariantAlias = entityVariant.Alias,
                        ParentId = parentId,
                        Id = updatedEntity.Id
                    };

                case CrudType.Delete:

                    await collection.Repository._DeleteAsync(id, parentId);
                    return new ReloadCommand();

                case CrudType.None:
                    return new NoOperationCommand();

                case CrudType.Refresh:
                    return new ReloadCommand();

                case CrudType.Create:
                default:
                    throw new InvalidOperationException();
            }
        }
    }
}
