using './main.bicep'

param location = 'swedencentral'
param operatorObjectId = '4abf75c1-8a34-4138-a0a5-651cf36ca396'
param developerIp = readEnvironmentVariable('KANBAN_DEVELOPER_IP')
