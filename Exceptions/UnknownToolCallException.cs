using System;
public class UnknownToolCallException : Exception {
	public UnknownToolCallException(string message) : base(message) { }
}
