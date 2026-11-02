"""Deployment helper script for Azure Container Apps.
Receives service argument ('frontend' | 'api') and immutable IMAGE_NAME; creates or replaces
the Container App configuration via the Azure REST API using Consumption workload profile.
"""
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path


def main():
    if len(sys.argv) < 2:
        raise SystemExit('Usage: python3 scripts/deploy-app.py <frontend|api>')

    service = sys.argv[1].lower()
    if service not in ('frontend', 'api'):
        raise SystemExit('Error: Choose "frontend" or "api"')

    e = os.environ
    required = [
        'AZURE_SUBSCRIPTION_ID',
        'RESOURCE_GROUP',
        'CONTAINER_ENVIRONMENT',
        'FRONTEND_APP',
        'API_APP',
        'ACR_SERVER',
        'RUNTIME_IDENTITY_ID',
        'RUNTIME_CLIENT_ID',
        'BLOB_SERVICE_URI',
        'BLOB_CONTAINER',
        'IMAGE_NAME',
    ]

    for key in required:
        if not e.get(key):
            raise SystemExit(f'Missing required environment setting: {key}')

    if service == 'api' and not e.get('SQL_CONNECTION_STRING'):
        raise SystemExit('Missing SQL_CONNECTION_STRING secret for api service')

    base = f"/subscriptions/{e['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{e['RESOURCE_GROUP']}"
    name = e['FRONTEND_APP' if service == 'frontend' else 'API_APP']
    identity = e['RUNTIME_IDENTITY_ID']
    location = e.get('AZURE_LOCATION', 'eastus2')

    env = [
        {'name': 'ASPNETCORE_HTTP_PORTS', 'value': '8080'},
        {'name': 'AZURE_CLIENT_ID', 'value': e['RUNTIME_CLIENT_ID']},
        {'name': 'BlobServiceUri', 'value': e['BLOB_SERVICE_URI']},
        {'name': 'BlobContainer', 'value': e['BLOB_CONTAINER']},
    ]

    secrets = []
    if service == 'api':
        secrets = [{'name': 'db-connection', 'value': e['SQL_CONNECTION_STRING']}]
        env.append({'name': 'ConnectionStrings__DefaultConnection', 'secretRef': 'db-connection'})

    doc = {
        'location': location,
        'identity': {
            'type': 'UserAssigned',
            'userAssignedIdentities': {identity: {}}
        },
        'properties': {
            'environmentId': f"{base}/providers/Microsoft.App/managedEnvironments/{e['CONTAINER_ENVIRONMENT']}",
            'configuration': {
                'activeRevisionsMode': 'Single',
                'maxInactiveRevisions': 10,
                'secrets': secrets,
                'registries': [{'server': e['ACR_SERVER'], 'identity': identity}],
                'ingress': {
                    'external': True,
                    'targetPort': 8080,
                    'transport': 'auto',
                    'allowInsecure': False
                }
            },
            'template': {
                'containers': [{
                    'name': service,
                    'image': e['IMAGE_NAME'],
                    'resources': {
                        'cpu': 0.25,
                        'memory': '0.5Gi'
                    },
                    'env': env
                }],
                'scale': {
                    'minReplicas': 0,
                    'maxReplicas': 1
                }
            }
        }
    }

    url = f"https://management.azure.com{base}/providers/Microsoft.App/containerApps/{name}?api-version=2025-07-01"

    with tempfile.TemporaryDirectory() as folder:
        path = Path(folder) / 'app.json'
        path.write_text(json.dumps(doc))
        try:
            path.chmod(0o600)
        except OSError:
            pass

        # Capture output so ARM's secret-bearing response never enters CI logs
        az_cmd = 'az.cmd' if sys.platform == 'win32' else 'az'
        result = subprocess.run(
            [az_cmd, 'rest', '--method', 'PUT', '--url', url, '--body', '@' + str(path),
             '--only-show-errors', '--output', 'none'],
            capture_output=True,
            text=True
        )

        if result.returncode != 0:
            error = result.stderr
            if e.get('SQL_CONNECTION_STRING'):
                error = error.replace(e['SQL_CONNECTION_STRING'], '[REDACTED]')
            raise SystemExit(error)

    print(f"Deployment submitted for {name}; inspect revision readiness before declaring success.")


if __name__ == '__main__':
    main()
