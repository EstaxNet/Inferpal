Inferpal {version} - a local AI agent for your editor
=====================================================

Inferpal is a coding assistant that runs on your own machine: it reads and edits your files, explores and searches
your code, runs commands and tests, with a model served by Ollama, LM Studio or any OpenAI-compatible server. No cloud,
no telemetry, no API key required. By default, it asks before every change it makes.

This archive is Inferpal as an Agent Client Protocol (ACP) agent, for Zed, the JetBrains IDEs, Neovim, Emacs and any
other ACP client. Visual Studio and VS Code have their own extensions.

What is in it
  - {executable}: the agent, with its own .NET runtime inside
  - {sqlite}: the database engine of the code search, loaded by the agent
  - LICENSE.txt: the GNU General Public License v3 the program is distributed under
  - README.txt: this file

Getting started
  1. Keep these files together, in any folder.
  2. Tell your editor to run the agent with the argument --acp, giving the full path of the executable.
  3. Start a thread with Inferpal. If no model server is set up yet, the first thread asks.
     Run the executable with --settings to open the settings in your browser.

Documentation (setup for each editor, settings, troubleshooting):
  {docs}

Source code, issues and releases:
  https://github.com/EstaxNet/Inferpal
