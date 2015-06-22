﻿using PPD.XLinq.TranslateModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Xinchen.Utils;
namespace PPD.XLinq.Provider.SqlServer2008R2.Parser
{
    public class SelectExpressionVisitor : ExpressionVisitorBase
    {
        IReadOnlyCollection<MemberInfo> _aliaMembers;
        IReadOnlyCollection<Expression> _columnParams;
        private Type _elementType;
        private Dictionary<string, Join> _Joins;
        private Stack<Expression> _memberExpressions = new Stack<Expression>();
        public List<Column> Columns { get; private set; }

        public SelectExpressionVisitor(TranslateContext context)
            : base(context)
        {
            // TODO: Complete member initialization
            this._elementType = context.EntityType;
            this._Joins = context.Joins;
            Columns = new List<Column>();
        }

        void ParseEntityType(Type type)
        {
            var tableSechma = TableInfoManager.GetTable(type);
            var tableInfo = new Table()
            {
                DataBase = tableSechma.DataBase,
                Name = tableSechma.Name,
                Type = tableSechma.Type
            };
            var columns = tableSechma.Columns;
            foreach (var columnValue in columns.Values)
            {
                var column = new Column();
                column.Name = columnValue.Name;
                column.DataType = columnValue.PropertyInfo.PropertyType;
                column.MemberInfo = columnValue.PropertyInfo;
                column.Table = tableInfo;
                Columns.Add(column);
                Context.Columns.Add(columnValue.Name, column);
            }
        }

        public override Expression Visit(Expression node)
        {
            if (node == null)
            {
                ParseEntityType(_elementType);
                return node;
            }
            _memberExpressions.Push(node);
            return base.Visit(node);
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            var type = node.Type;
            while (!TableInfoManager.IsEntity(type))
            {
                var exp = _memberExpressions.Pop();
                type = exp.Type;
            }
            ParseEntityType(type);
            return node;
        }

        protected override Expression VisitMemberInit(MemberInitExpression node)
        {
            foreach (MemberAssignment binding in node.Bindings)
            {
                var visitor = new MemberExpressionVisitor(Context);
                visitor.Visit(binding.Expression);
                visitor.SelectedColumn.Alias = binding.Member.Name;
                Columns.Add(visitor.SelectedColumn);
                Context.Columns.Add(binding.Member.Name, visitor.SelectedColumn);
            }
            return node;
        }

        protected override Expression VisitNew(NewExpression node)
        {
            for (int i = 0; i < node.Arguments.Count; i++)
            {
                var visitor = new MemberExpressionVisitor(Context);
                var arg = node.Arguments[i];
                var member = node.Members[i];
                visitor.Visit(arg);
                visitor.SelectedColumn.Alias = member.Name;
                Columns.Add(visitor.SelectedColumn);
                Context.Columns.Add(member.Name, visitor.SelectedColumn);
            }
            return node;
        }

        class MemberExpressionVisitor : ExpressionVisitorBase
        {
            public Column SelectedColumn { get; private set; }
            public object Value { get; set; }
            //public MemberExpressionType MemberExpressionType { get; private set; }
            Stack<MemberInfo> _memberInfoStack = new Stack<MemberInfo>();
            private MemberInfo _tableMember;
            private Dictionary<string, Join> _Joins;
            public MemberExpressionVisitor(TranslateContext context)
                : base(context)
            {
                _Joins = context.Joins;
                SelectedColumn = new Column();
            }
            public object GetValue(MemberInfo memberInfo, object obj)
            {
                if (memberInfo.MemberType == MemberTypes.Field)
                {
                    return ((FieldInfo)memberInfo).GetValue(obj);
                }
                return ((PropertyInfo)memberInfo).GetValue(obj);
            }

            protected override Expression VisitMember(MemberExpression node)
            {
                _memberInfoStack.Push(node.Member);
                if (node.Expression == null)
                {
                    //静态成员调用
                    throw new NotSupportedException("Select子句中不允许调用方法");
                }
                else if (node.Expression.NodeType == ExpressionType.Constant)
                {
                    //实例成员调用
                    //MemberExpressionType = MemberExpressionType.Object;
                    return node;
                }
                return base.VisitMember(node);
            }

            SchemaModel.Table GetTable()
            {
                _tableMember = _memberInfoStack.Pop();
                var tableType = ((PropertyInfo)_tableMember).PropertyType;
                return GetTable(tableType);
            }

            SchemaModel.Table GetTable(Type tableType)
            {
                if (ParserUtils.IsAnonymousType(tableType))
                {
                    _tableMember = _memberInfoStack.Pop();
                    tableType = ((PropertyInfo)_tableMember).PropertyType;
                    return GetTable(tableType);
                }
                return TableInfoManager.GetTable(tableType); ;
            }

            protected override Expression VisitConstant(ConstantExpression node)
            {
                throw new NotSupportedException("Select子句中不允许直接写常量");
            }

            string GetConverter()
            {
                if (_memberInfoStack.Count > 0)
                {
                    var propertyInfo = (PropertyInfo)_memberInfoStack.Pop();

                    if (propertyInfo.DeclaringType.FullName.StartsWith("System.Nullable") && propertyInfo.DeclaringType.Assembly.GlobalAssemblyCache)
                    {
                        //可空属性的Value访问，忽略不管
                        if (_memberInfoStack.Count <= 0)
                        {
                            return null;
                        }
                        propertyInfo = (PropertyInfo)_memberInfoStack.Pop();
                    }
                    if (propertyInfo.DeclaringType != typeof(DateTime) || propertyInfo.Name != "Date")
                    {
                        throw new Exception("除x=>x.Time.Date以外，不支持其他的");
                    }
                    return "CONVERT(DATE,{0},211)";
                }
                return null;
            }

            Table CreateTable(string alias, string db, string name, Type type)
            {
                return new Table()
                {
                    Alias = alias,
                    DataBase = db,
                    Name = name,
                    Type = type
                };
            }

            protected override Expression VisitParameter(ParameterExpression node)
            {
                //MemberExpressionType = MemberExpressionType.Column;
                if (TableInfoManager.IsEntity(node.Type))
                {
                    //弹出第一个参数，一般是列
                    var table = GetTable(node.Type);
                    var _memberInfo = _memberInfoStack.Pop();
                    SelectedColumn.DataType = ((PropertyInfo)_memberInfo).PropertyType;
                    SelectedColumn.Name = table.Columns.Get(_memberInfo.Name).Name;
                    SelectedColumn.MemberInfo = _memberInfo;
                    var tableAlias = node.Name;
                    if (_Joins != null)
                    {
                        if (_Joins.ContainsKey(tableAlias))
                        {
                            tableAlias = _Joins[tableAlias].Right.Table.Alias;
                        }
                    }
                    else
                    {
                        tableAlias = table.Name;
                    }
                    SelectedColumn.Table = CreateTable(tableAlias, table.DataBase, table.Name, table.Type);
                    SelectedColumn.Converter = GetConverter();
                }
                else
                {
                    var tableInfo = GetTable();
                    var columnMember = _memberInfoStack.Pop();
                    var columnType = ((PropertyInfo)columnMember).PropertyType;
                    var tableAlias = _tableMember.Name;
                    if (_Joins != null)
                    {
                        if (_Joins.ContainsKey(tableAlias))
                        {
                            tableAlias = _Joins[tableAlias].Right.Table.Alias;
                        }
                    }
                    else
                    {
                        tableAlias = tableInfo.Name;
                    }
                    var table = CreateTable(tableAlias, tableInfo.DataBase, tableInfo.Name, tableInfo.Type);
                    SelectedColumn = new Column()
                    {
                        DataType = columnType,
                        Name = tableInfo.Columns.Get(columnMember.Name).Name,
                        Table = table,
                        MemberInfo = columnMember
                    };
                    SelectedColumn.Converter = GetConverter();
                }
                return node;
            }
        }
    }
}
