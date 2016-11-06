using System;
using System.Threading.Tasks;

namespace SillyDPI
{
	internal static class TaskExt
	{
		public static Task LogExceptions(this Task task)
		{
			task.ContinueWith(t => { Console.WriteLine(task.Exception.ToString()); }, TaskContinuationOptions.OnlyOnFaulted);
			return task;
		}

		public static Task SuppressExceptions(this Task task)
		{
			task.ContinueWith(t => { var ex = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
			return task;
		}

		public static Task InvokeOnException(this Task task, Action<AggregateException> action)
		{
			if (action != null) task.ContinueWith(t => { action(t.Exception); }, TaskContinuationOptions.OnlyOnFaulted);
			else return SuppressExceptions(task);
			return task;
		}
	}
}
