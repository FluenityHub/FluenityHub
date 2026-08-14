# Privacy Policy for FluenityHub

**Effective Date:** August 14, 2026  
**Last Updated:** August 14, 2026


## Overview

**FluenityHub** is an open-source Windows 11 application for managing Unity projects, Editor installations, modules, templates, backups, licensing workflows, and source control integrations.

FluenityHub follows a **local-first, privacy-by-design** approach. It does not operate a backend service that collects or stores application usage data.

## Data Collection

FluenityHub does **not** include:

- Usage telemetry or analytics
- Advertising or tracking
- Device fingerprinting
- Behavioral profiling
- Third-party analytics SDKs
- Automatic crash-report uploads

FluenityHub does not sell, rent, or monetize personal information.

## Local Data

Application settings and metadata are stored locally on your device, including:

- Preferences and configuration
- Project paths, tags, groups, and display names
- Unity installation information
- Template and backup settings

FluenityHub data may be stored under:

```text
%APPDATA%\FluenityHub\
```

FluenityHub may also read configuration from supported applications such as Unity Hub when required for integration.

Credentials such as Personal Access Tokens may be stored using **Windows Credential Manager** and protected by Windows security mechanisms.

## Network Communications

FluenityHub primarily operates locally. Network access occurs only when required for application features or user-requested actions.

| Service | Purpose |
| :--- | :--- |
| **GitHub** | Checking for FluenityHub releases or updates |
| **Unity services** | Retrieving and downloading Unity Editors, modules, and related files |
| **GitHub / GitLab / Git remotes** | Source control operations requested by the user |

These connections are made directly between your device and the relevant third-party service.

Third-party services may receive standard network information such as your IP address, request headers, authentication information, or repository information as required to provide their services.

FluenityHub does not proxy this traffic through FluenityHub-operated servers.

## Third-Party Services

Third-party services used with FluenityHub are governed by their own privacy policies.

- [Unity Privacy Policy](https://unity.com/legal/privacy-policy)
- [GitHub Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-privacy-statement)
- [GitLab Privacy Statement](https://about.gitlab.com/privacy/)

FluenityHub is not responsible for the privacy practices of third-party services.

## Changes

Any updates to this privacy policy will be reflected in the app's GitHub repository.

## Contact

For privacy questions, open an issue at:

[https://github.com/FluenityHub/FluenityHub/issues](https://github.com/FluenityHub/FluenityHub/issues)