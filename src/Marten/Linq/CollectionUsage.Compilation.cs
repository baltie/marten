#nullable enable

using System;
using System.Linq;
using JasperFx.Core.Reflection;
using Marten.Exceptions;
using Marten.Internal;
using Marten.Internal.Storage;
using Marten.Linq.Includes;
using Marten.Linq.Members;
using Marten.Linq.Parsing;
using Marten.Linq.SqlGeneration;

namespace Marten.Linq;

public partial class CollectionUsage
{
    private bool _hasCompiledMany;

    public Statement BuildTopStatement(IMartenSession session, IQueryableMemberCollection collection,
        IDocumentStorage storage, QueryStatistics? statistics)
    {
        Statement top;

        var statement = new SelectorStatement
        {
            SelectClause = storage, Limit = _limit, Offset = _offset, IsDistinct = IsDistinct
        };

        top = statement;

        foreach (var ordering in OrderingExpressions)
        {
            statement.Ordering.Expressions.Add(ordering.BuildExpression(collection));
        }

        statement.ParseWhereClause(WhereExpressions, session, collection, storage);

        ParseIncludes(collection, session);
        if (Includes.Any())
        {
            var inner = statement.Top();
            var selectionStatement = inner.SelectorStatement();

            if (inner is SelectorStatement { SelectClause: IDocumentStorage } select)
            {
                select.SelectClause = storage.SelectClauseWithDuplicatedFields;
            }

            // QueryStatistics has to be applied to the inner, selector statement
            if (statistics != null)
            {
                var innerSelect = inner.SelectorStatement();
                innerSelect.SelectClause = innerSelect.SelectClause.UseStatistics(statistics);
            }

            var temp = new TemporaryTableStatement(inner, session);
            foreach (var include in Includes) include.AppendStatement(temp, session);

            temp.AddToEnd(new PassthroughSelectStatement(temp.ExportName, selectionStatement.SelectClause));

            top = temp;
            statement = top.SelectorStatement();
        }

        if (SelectExpression != null)
        {
            var visitor = new SelectorVisitor(statement, collection, _options.Serializer());
            visitor.Visit(SelectExpression);

            if (Inner != null)
            {
                IsAny = IsAny || Inner.IsAny;
                SingleValueMode ??= Inner.SingleValueMode;
                IsDistinct = Inner.IsDistinct;
                statement.Limit ??= Inner._limit;
                statement.Offset ??= Inner._offset;
            }
        }

        // Deal with query statistics at the last minute
        if (statistics != null)
        {
            statement.SelectClause = statement.SelectClause.UseStatistics(statistics);
        }

        ProcessSingleValueModeIfAny(statement.SelectorStatement(), session);

        statement = compileNext(session, collection, statement, statistics).SelectorStatement();

        // THIS CAN BE A PROBLEM IF IT'S DONE TOO SOON
        if (IsDistinct)
        {
            if (SelectExpression != null && OrderingExpressions.Any(x => x.IsTransformed))
            {
                throw new BadLinqExpressionException(
                    "Marten is unable to build a query with a Distinct() + Select() + a 'transformed' OrderBy(). You will have to resort to SQL for this query");
            }

            statement.ApplySqlOperator("DISTINCT");
        }

        return statement.Top();
    }


    public Statement BuildSelectManyStatement(IMartenSession session, IQueryableMemberCollection collection,
        ISelectClause selectClause, QueryStatistics? statistics)
    {
        var statement = new SelectorStatement
        {
            SelectClause = selectClause ?? throw new ArgumentNullException(nameof(selectClause))
        };

        ConfigureSelectManyStatement(session, collection, statement, statistics);

        if (IsDistinct)
        {
            statement.ApplySqlOperator("DISTINCT");
        }

        return statement;
    }

    internal Statement ConfigureSelectManyStatement(IMartenSession session, IQueryableMemberCollection collection,
        SelectorStatement statement, QueryStatistics? statistics)
    {
        statement.Limit = _limit;
        statement.Offset = _offset;
        statement.IsDistinct = IsDistinct;

        foreach (var ordering in OrderingExpressions)
            statement.Ordering.Expressions.Add(ordering.BuildExpression(collection));

        statement.ParseWhereClause(WhereExpressions, session, collection);

        if (SelectExpression != null)
        {
            var visitor = new SelectorVisitor(statement, collection, _options.Serializer());
            visitor.Visit(SelectExpression);

            if (Inner != null)
            {
                IsAny = IsAny || Inner.IsAny;
                SingleValueMode ??= Inner.SingleValueMode;
                IsDistinct = Inner.IsDistinct;
                statement.Limit ??= Inner._limit;
                statement.Offset ??= Inner._offset;
            }
        }

        // Add Includes here!

        ProcessSingleValueModeIfAny(statement, session);

        compileNext(session, collection, statement, statistics);

        return statement.Top();
    }


    private Statement compileNext(IMartenSession session, IQueryableMemberCollection collection,
        SelectorStatement statement, QueryStatistics? statistics)
    {
        if (SelectMany != null)
        {
            var selection = statement.SelectorStatement();
            var collectionMember = (ICollectionMember)collection.MemberFor(SelectMany);

            // You might now already have another collection usage if the statement ends with
            // SelectMany()

            if (Inner == null)
            {
                if (SingleValueMode == Marten.Linq.Parsing.SingleValueMode.Any)
                {
                    var filter = collectionMember.NotEmpty;
                    selection.Wheres.Add(filter);
                    selection.SelectClause = new AnySelectClause(selection.SelectClause.FromObject);
                }
                else if (SingleValueMode == Marten.Linq.Parsing.SingleValueMode.Count)
                {
                    selection.SelectClause = new NewScalarSelectClause<int>(
                        $"jsonb_array_length({collectionMember.JSONBLocator})", selection.SelectClause.FromObject);

                    selection.ApplyAggregateOperator("SUM");
                }
                else
                {
                    var next = new CollectionUsage(_options, collectionMember.MemberType);
                    return next.CompileSelectMany(session, this, selection, collectionMember, statistics);
                }
            }
            else
            {
                return Inner.CompileSelectMany(session, this, selection, collectionMember, statistics);
            }
        }
        else
        {
            Inner?.CompileAsChild(this);
        }

        return statement;
    }

    public Statement CompileSelectMany(IMartenSession session, CollectionUsage parent,
        SelectorStatement parentStatement, ICollectionMember collectionMember, QueryStatistics? statistics)
    {
        if (_hasCompiledMany)
        {
            return parentStatement;
        }

        _hasCompiledMany = true;

        parentStatement.Mode = StatementMode.CommonTableExpression;
        parentStatement.ExportName = session.NextTempTableName() + "CTE";

        parentStatement.SelectClause =
            collectionMember.BuildSelectClauseForExplosion(parentStatement.SelectClause.FromObject);


        // THINK THIS IS TOO SOON. MUCH OF THE LOGIC NEEDS TO GO IN THIS INSTEAD!!!
        var childStatement = collectionMember.BuildSelectManyStatement(this, session, parentStatement, statistics);
        var childSelector = childStatement.SelectorStatement();

        // ParseIncludes(collection, session);
        // if (Includes.Any())
        // {
        //     var inner = statement.Top();
        //     var selectionStatement = inner.SelectorStatement();
        //
        //     if (inner is SelectorStatement { SelectClause: IDocumentStorage } select)
        //     {
        //         select.SelectClause = storage.SelectClauseWithDuplicatedFields;
        //     }
        //
        //     // QueryStatistics has to be applied to the inner, selector statement
        //     if (statistics != null)
        //     {
        //         var innerSelect = inner.SelectorStatement();
        //         innerSelect.SelectClause = innerSelect.SelectClause.UseStatistics(statistics);
        //     }
        //
        //     var temp = new TemporaryTableStatement(inner, session);
        //     foreach (var include in Includes) include.AppendStatement(temp, session);
        //
        //     temp.AddToEnd(new PassthroughSelectStatement(temp.ExportName, selectionStatement.SelectClause));
        //
        //     top = temp;
        //     statement = top.SelectorStatement();
        // }

        if (IsDistinct)
        {
            if (childSelector.SelectClause is IScalarSelectClause c)
            {
                c.ApplyOperator("DISTINCT");
                parentStatement.AddToEnd(childStatement.Top());
            }
            else if (childSelector.SelectClause is ICountClause count)
            {
                if (collectionMember is IQueryableMemberCollection members)
                {
                    // It places itself at the back in this constructor function
                    var distinct = new DistinctSelectionStatement(parentStatement, count, session);
                    compileNext(session, members, distinct.SelectorStatement(), statistics);
                }
                else
                {
                    throw new BadLinqExpressionException("See https://github.com/JasperFx/marten/issues/2704");
                }

                return parentStatement;
            }
        }
        else
        {
            parentStatement.AddToEnd(childStatement.Top());
        }

        return compileNext(session, collectionMember as IQueryableMemberCollection, childSelector, statistics);
    }

    public void CompileAsChild(CollectionUsage parent)
    {
        if (ElementType.IsSimple() || ElementType == typeof(Guid) || ElementType == typeof(Guid?))
        {
            if (IsDistinct)
            {
                parent.IsDistinct = IsDistinct;
            }
        }
    }

    internal void ProcessSingleValueModeIfAny(SelectorStatement statement, IMartenSession session)
    {
        if (IsAny || SingleValueMode == Marten.Linq.Parsing.SingleValueMode.Any)
        {
            statement.SelectClause = new AnySelectClause(statement.SelectClause.FromObject);
            statement.Limit = 1;
            return;
        }

        if (SingleValueMode.HasValue)
        {
            switch (SingleValueMode)
            {
                case Marten.Linq.Parsing.SingleValueMode.First:
                    statement.SingleValue = true;
                    statement.CanBeMultiples = true;
                    statement.ReturnDefaultWhenEmpty = false;
                    statement.Limit ??= 1;
                    break;

                case Marten.Linq.Parsing.SingleValueMode.FirstOrDefault:
                    statement.SingleValue = true;
                    statement.CanBeMultiples = true;
                    statement.ReturnDefaultWhenEmpty = true;
                    statement.Limit ??= 1;
                    break;

                case Marten.Linq.Parsing.SingleValueMode.Single:
                    statement.SingleValue = true;
                    statement.CanBeMultiples = false;
                    statement.ReturnDefaultWhenEmpty = false;
                    statement.Limit ??= 2;
                    break;

                case Marten.Linq.Parsing.SingleValueMode.SingleOrDefault:
                    statement.SingleValue = true;
                    statement.CanBeMultiples = false;
                    statement.ReturnDefaultWhenEmpty = true;
                    statement.Limit ??= 2;
                    break;

                case Marten.Linq.Parsing.SingleValueMode.Count:
                    // Invalid to be using OrderBy() while also using Count() in
                    // PostgreSQL. Thank you Hot Chocolate.
                    statement.Ordering.Expressions.Clear();

                    if (statement.IsDistinct)
                    {
                        statement.ConvertToCommonTableExpression(session);
                        var count = new SelectorStatement
                        {
                            SelectClause = new CountClause<int>(statement.ExportName)
                        };

                        statement.AddToEnd(count);
                    }

                    statement.SelectClause = new CountClause<int>(statement.SelectClause.FromObject);

                    break;

                case Marten.Linq.Parsing.SingleValueMode.LongCount:
                    // Invalid to be using OrderBy() while also using Count() in
                    // PostgreSQL. Thank you Hot Chocolate.
                    statement.Ordering.Expressions.Clear();

                    if (statement.IsDistinct)
                    {
                        statement.ConvertToCommonTableExpression(session);
                        var count = new SelectorStatement
                        {
                            SelectClause = new CountClause<long>(statement.ExportName)
                        };

                        statement.AddToEnd(count);
                    }

                    statement.SelectClause = new CountClause<long>(statement.SelectClause.FromObject);
                    break;

                case Marten.Linq.Parsing.SingleValueMode.Average:
                    statement.ApplyAggregateOperator("AVG");
                    break;

                case Marten.Linq.Parsing.SingleValueMode.Max:
                    statement.ApplyAggregateOperator("MAX");
                    break;

                case Marten.Linq.Parsing.SingleValueMode.Min:
                    statement.ApplyAggregateOperator("MIN");
                    break;

                case Marten.Linq.Parsing.SingleValueMode.Sum:
                    statement.ApplyAggregateOperator("SUM");
                    break;

                default:
                    throw new NotImplementedException($"Whoa pardner, don't have this {SingleValueMode} yet!");
            }
        }
    }
}
